using System.Diagnostics;
using System.IO;
using System.Text.Json;
namespace CardViewer;

public sealed class PipelineLog
{
    private readonly object sync = new();
    private readonly string path;
    public PipelineLog(string path)
    {
        this.path = path;
    }
    public void Write(PipelineTiming timing)
    {
        try
        {
            lock (sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.AppendAllText(path, JsonSerializer.Serialize(new
                {
                    Utc = DateTime.UtcNow,
                    Timing = timing
                }) + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
