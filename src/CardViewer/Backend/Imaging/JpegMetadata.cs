using System.Buffers.Binary;
using System.IO;

namespace CardViewer;

// Reads JPEG markers and EXIF IFD1 directly; never scans the compressed main image.
internal sealed class JpegMetadata
{
    internal const int ReadBudget = 32 * 1024 - 1;
    private readonly Stream stream;
    private readonly CancellationToken token;
    private int bytesRead;
    private long tiffStart;
    private long exifEnd;
    private bool littleEndian;

    private JpegMetadata(Stream stream, CancellationToken token)
    {
        this.stream = stream;
        this.token = token;
    }

    internal static async Task<(JpegPreview? Preview, int BytesRead)> ReadAsync(Stream stream, CancellationToken token)
    {
        var reader = new JpegMetadata(stream, token);
        try
        {
            return (await reader.ReadPreviewAsync(), reader.bytesRead);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException)
        {
            return (null, reader.bytesRead);
        }
    }

    private async Task<byte[]> ReadAtAsync(long offset, int count)
    {
        token.ThrowIfCancellationRequested();
        if (offset < 0 || count < 0 || offset > stream.Length - count || count > ReadBudget - bytesRead)
            throw new InvalidDataException("JPEG preview exceeds the bounded read budget or file bounds.");

        stream.Position = offset;
        var bytes = new byte[count];
        int filled = 0;
        while (filled < count)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(filled), token);
            bytesRead += read;
            if (read == 0)
                throw new EndOfStreamException();
            filled += read;
        }
        return bytes;
    }

    private async Task<JpegPreview?> ReadPreviewAsync()
    {
        var signature = await ReadAtAsync(0, 2);
        if (signature[0] != 0xff || signature[1] != 0xd8)
            return null;

        long position = 2;
        for (int markers = 0; markers < 128; markers++)
        {
            var marker = await ReadAtAsync(position, 2);
            if (marker[0] != 0xff)
                return null;
            if (marker[1] == 0xff)
            {
                position++;
                continue;
            }
            if (marker[1] is 0xda or 0xd9) // Stop at image data or end of image.
                return null;
            if (marker[1] is 0x01 or >= 0xd0 and <= 0xd7)
            {
                position += 2;
                continue;
            }

            var lengthBytes = await ReadAtAsync(position + 2, 2);
            int length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (length < 2 || position + 2 + length > stream.Length)
                return null;
            if (marker[1] == 0xe1 && length >= 16)
            {
                var header = await ReadAtAsync(position + 4, 6);
                if (header.AsSpan().SequenceEqual("Exif\0\0"u8))
                {
                    tiffStart = position + 10;
                    exifEnd = position + 2 + length;
                    var preview = await ReadExifAsync();
                    if (preview != null)
                        return preview;
                }
            }
            position += 2 + length;
        }
        return null;
    }

    private async Task<byte[]> ReadTiffAsync(uint offset, int count)
    {
        long position = tiffStart + offset;
        if (position < tiffStart || position > exifEnd - count)
            throw new InvalidDataException("EXIF thumbnail points outside its segment.");
        return await ReadAtAsync(position, count);
    }

    private ushort U16(ReadOnlySpan<byte> bytes) => littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private uint U32(ReadOnlySpan<byte> bytes) => littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private async Task<(Dictionary<ushort, uint> Tags, uint Next)> ReadDirectoryAsync(uint offset)
    {
        var countBytes = await ReadTiffAsync(offset, 2);
        int count = U16(countBytes);
        if (count > 128)
            throw new InvalidDataException("Too many EXIF directory entries.");
        var entries = await ReadTiffAsync(checked(offset + 2), count * 12 + 4);
        var tags = new Dictionary<ushort, uint>();
        for (int i = 0; i < count; i++)
        {
            int entry = i * 12;
            ushort type = U16(entries.AsSpan(entry + 2));
            if (U32(entries.AsSpan(entry + 4)) != 1 || type is not (3 or 4))
                continue;
            tags[U16(entries.AsSpan(entry))] = type == 3
                ? U16(entries.AsSpan(entry + 8))
                : U32(entries.AsSpan(entry + 8));
        }
        return (tags, U32(entries.AsSpan(count * 12)));
    }

    private async Task<JpegPreview?> ReadExifAsync()
    {
        var header = await ReadTiffAsync(0, 8);
        littleEndian = header[0] == 'I' && header[1] == 'I';
        if (!littleEndian && !(header[0] == 'M' && header[1] == 'M'))
            return null;
        if (U16(header.AsSpan(2)) != 42)
            return null;
        uint first = U32(header.AsSpan(4));
        if (first < 8)
            return null;
        var main = await ReadDirectoryAsync(first);
        if (main.Next == 0)
            return null;
        var thumbnail = await ReadDirectoryAsync(main.Next);
        if (!thumbnail.Tags.TryGetValue(0x0201, out uint offset) ||
            !thumbnail.Tags.TryGetValue(0x0202, out uint length) || length < 4 || length > ReadBudget)
            return null;

        var bytes = await ReadTiffAsync(offset, checked((int)length));
        if (bytes[0] != 0xff || bytes[1] != 0xd8)
            return null;
        uint orientation = thumbnail.Tags.GetValueOrDefault((ushort)0x0112, main.Tags.GetValueOrDefault((ushort)0x0112, 1u));
        return new JpegPreview(bytes, orientation is >= 1 and <= 8 ? (int)orientation : 1);
    }
}
