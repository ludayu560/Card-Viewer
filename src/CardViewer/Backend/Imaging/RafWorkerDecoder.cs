using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace CardViewer;

// Native LibRaw has no reliable mid-decode cancellation. Isolate it so navigation
// can reclaim its CPU/memory immediately without occupying a HIF worker slot.
public static class RafWorkerDecoder
{
    internal static async Task<BitmapSource> DecodeAsync(byte[] source, CancellationToken token, Action<Process>? started = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "CardViewer-raw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "source.raf");
        var output = Path.Combine(folder, "pixels.bgra");
        try
        {
            await File.WriteAllBytesAsync(input, source, token);
            var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate decoder host.");
            var start = new ProcessStartInfo(processPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            start.Environment["MAGICK_TEMPORARY_PATH"] = folder;
            start.Environment["TEMP"] = folder;
            start.Environment["TMP"] = folder;
            if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            start.ArgumentList.Add("--decode-raf-worker");
            start.ArgumentList.Add(input);
            start.ArgumentList.Add(output);
            token.ThrowIfCancellationRequested();
            using var process = new Process { StartInfo = start };
            process.Start();
            var error = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            using var cancellation = token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });
            try
            {
                try
                {
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
                started?.Invoke(process);
                await process.WaitForExitAsync();
                var message = await error;
                await stdout;
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new InvalidDataException("RAW decoder failed: " + message.Trim());
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            await using var stream = File.OpenRead(output);
            var header = new byte[8];
            await stream.ReadExactlyAsync(header, token);
            int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header), height = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            long length = checked((long)width * height * 4);
            if (width <= 0 || height <= 0 || length > 512L * 1024 * 1024 || stream.Length != length + 8)
                throw new InvalidDataException("Invalid RAW decoder output.");
            var pixels = new byte[checked((int)length)];
            await stream.ReadExactlyAsync(pixels, token);
            token.ThrowIfCancellationRequested();
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, checked(width * 4));
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            // This unique directory was created above exclusively for this worker.
            // Include native scratch files left by a killed decoder; never recurse.
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
                    File.Delete(file);
                Directory.Delete(folder, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    public static int Run(string input, string output)
    {
        try
        {
            var pixels = FullImageDecoder.DecodePixels(File.ReadAllBytes(input), true, default);
            using var stream = File.Create(output);
            using var writer = new BinaryWriter(stream);
            writer.Write(pixels.Width);
            writer.Write(pixels.Height);
            writer.Write(pixels.Pixels);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
