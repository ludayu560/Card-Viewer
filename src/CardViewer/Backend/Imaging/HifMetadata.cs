using System.Buffers.Binary;
using System.IO;
using System.Text;
namespace CardViewer;

// Bounded ISO BMFF metadata reader. It seeks over mdat rather than scanning image payloads.
public static class HifMetadata
{
    private sealed record Box(string Type, int Start, int Data, int End);
    private static uint U32(byte[] b, int p)
    {
        Bounds(b, p, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p, 4));
    }
    private static ushort U16(byte[] b, int p)
    {
        Bounds(b, p, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p, 2));
    }
    private static void Bounds(byte[] b, int p, int n)
    {
        if (p < 0 || n < 0 || p > b.Length - n)
            throw new InvalidDataException("Truncated HEIF metadata.");
    }
    private static List<Box> Boxes(byte[] b, int start, int end)
    {
        var result = new List<Box>();
        while (start < end)
        {
            if (end - start < 8)
                throw new InvalidDataException("Truncated box.");
            long size = U32(b, start);
            int header = 8;
            if (size == 1)
            {
                Bounds(b, start, 16);
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(start + 8, 8)));
                header = 16;
            }
            if (size == 0)
                size = end - start;
            if (size < header || size > end - start)
                throw new InvalidDataException("Invalid box size.");
            result.Add(new(Encoding.ASCII.GetString(b, start + 4, 4), start, start + header, checked(start + (int)size)));
            start += checked((int)size);
        }
        return result;
    }
    public static async Task<(HifIndex Index, long Bytes)> ReadAsync(FileStream stream, CancellationToken token)
    {
        long bytes = 0;
        byte[]? meta = null;
        long metaOffset = 0;
        // Bound traversal and metadata allocation for malformed/unusual containers.
        for (int i = 0; i < 128 && stream.Position < stream.Length; i++)
        {
            var offset = stream.Position;
            var head = new byte[8];
            await stream.ReadExactlyAsync(head, token);
            bytes += 8;
            long size = U32(head, 0);
            int header = 8;
            if (size == 1)
            {
                var ext = new byte[8];
                await stream.ReadExactlyAsync(ext, token);
                bytes += 8;
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(ext));
                header = 16;
            }
            if (size == 0)
                size = stream.Length - offset;
            if (size < header || size > stream.Length - offset)
                throw new InvalidDataException("Invalid top-level box.");
            if (Encoding.ASCII.GetString(head, 4, 4) == "meta")
            {
                if (size - header > 1024 * 1024)
                    throw new NotSupportedException("HEIF metadata exceeds the 1 MB preview budget.");
                meta = new byte[checked((int)(size - header))];
                metaOffset = offset + header;
                await stream.ReadExactlyAsync(meta, token);
                bytes += meta.Length;
                break;
            }
            stream.Seek(offset + size, SeekOrigin.Begin);
        }
        if (meta == null)
            throw new NotSupportedException("No HEIF metadata box found.");
        var boxes = Boxes(meta, 4, meta.Length);
        var pitm = boxes.First(b => b.Type == "pitm");
        uint primary = meta[pitm.Data] == 0 ? U16(meta, pitm.Data + 4) : U32(meta, pitm.Data + 4);
        var iinf = boxes.First(b => b.Type == "iinf");
        var types = new Dictionary<uint, string>();
        foreach (var b in Boxes(meta, iinf.Data + (meta[iinf.Data] == 0 ? 6 : 8), iinf.End))
        {
            if (b.Type != "infe")
                continue;
            var v = meta[b.Data];
            if (v != 2 && v != 3)
                continue;
            uint id = v == 2 ? U16(meta, b.Data + 4) : U32(meta, b.Data + 4);
            int p = b.Data + (v == 2 ? 8 : 10);
            Bounds(meta, p, 4);
            types[id] = Encoding.ASCII.GetString(meta, p, 4);
        }
        var thumbnailIds = new HashSet<uint>();
        var iref = boxes.FirstOrDefault(b => b.Type == "iref");
        if (iref != null)
            foreach (var b in Boxes(meta, iref.Data + 4, iref.End))
            {
                if (b.Type != "thmb")
                    continue;
                int p = b.Data;
                int w = meta[iref.Data] == 0 ? 2 : 4;
                uint from = w == 2 ? U16(meta, p) : U32(meta, p);
                p += w;
                int n = U16(meta, p);
                p += 2;
                for (int i = 0; i < n; i++, p += w)
                    if ((w == 2 ? U16(meta, p) : U32(meta, p)) == primary)
                        thumbnailIds.Add(from);
            }
        var dimensions = new Dictionary<uint, (int, int)>();
        int rotation = 0;
        int? mirror = null;
        var iprp = boxes.FirstOrDefault(b => b.Type == "iprp");
        if (iprp != null)
        {
            var props = Boxes(meta, iprp.Data, iprp.End);
            var ipco = props.First(b => b.Type == "ipco");
            var table = Boxes(meta, ipco.Data, ipco.End);
            foreach (var ipma in props.Where(b => b.Type == "ipma"))
            {
                int p = ipma.Data;
                int version = meta[p];
                bool wide = (U32(meta, p) & 1) != 0;
                int entries = checked((int)U32(meta, p + 4));
                p += 8;
                if (entries > 4096)
                    throw new InvalidDataException("Too many property associations.");
                for (int i = 0; i < entries; i++)
                {
                    uint id = version < 1 ? U16(meta, p) : U32(meta, p);
                    p += version < 1 ? 2 : 4;
                    Bounds(meta, p, 1);
                    int n = meta[p++];
                    for (int j = 0; j < n; j++)
                    {
                        Bounds(meta, p, wide ? 2 : 1);
                        int ix = wide ? U16(meta, p) & 0x7fff : meta[p] & 0x7f;
                        p += wide ? 2 : 1;
                        if (ix == 0)
                            continue;
                        if (ix > table.Count)
                            throw new InvalidDataException("Invalid property reference.");
                        var prop = table[ix - 1];
                        if (prop.Type == "ispe")
                            dimensions[id] = (checked((int)U32(meta, prop.Data + 4)), checked((int)U32(meta, prop.Data + 8)));
                        if (id == primary && prop.Type == "irot")
                        {
                            Bounds(meta, prop.Data, 1);
                            rotation = (meta[prop.Data] & 3) * 90;
                        }
                        if (id == primary && prop.Type == "imir")
                        {
                            Bounds(meta, prop.Data, 1);
                            mirror = meta[prop.Data] & 1;
                        }
                    }
                }
            }
        }
        var iloc = boxes.First(b => b.Type == "iloc");
        int q = iloc.Data;
        int ver = meta[q];
        if (ver > 2)
            throw new NotSupportedException("Unsupported iloc version.");
        q += 4;
        Bounds(meta, q, 2);
        int os = meta[q] >> 4, ls = meta[q] & 15, bs = meta[q + 1] >> 4, xs = ver > 0 ? meta[q + 1] & 15 : 0;
        q += 2;
        ulong Number(int n)
        {
            if (n > 8)
                throw new InvalidDataException("Invalid extent width.");
            Bounds(meta, q, n);
            ulong value = 0;
            for (int i = 0; i < n; i++)
                value = (value << 8) | meta[q++];
            return value;
        }
        int count = checked((int)Number(ver < 2 ? 2 : 4));
        if (count > 4096)
            throw new InvalidDataException("Too many HEIF items.");
        var previews = new List<EmbeddedJpeg>();
        var idat = boxes.FirstOrDefault(b => b.Type == "idat");
        for (int i = 0; i < count; i++)
        {
            uint id = checked((uint)Number(ver < 2 ? 2 : 4));
            int method = ver > 0 ? (int)Number(2) & 15 : 0;
            int reference = (int)Number(2);
            ulong baseOffset = Number(bs);
            int extents = (int)Number(2);
            for (int j = 0; j < extents; j++)
            {
                if (ver > 0 && xs > 0)
                    Number(xs);
                ulong offset = Number(os), length = Number(ls);
                if (extents != 1 || reference != 0 || !thumbnailIds.Contains(id) || !types.TryGetValue(id, out var type) || type != "jpeg" || !dimensions.TryGetValue(id, out var dim))
                    continue;
                if (method != 0 && !(method == 1 && idat != null))
                    continue;
                long absolute = checked((long)(baseOffset + offset) + (method == 1 ? metaOffset + idat!.Data : 0));
                if (length == 0 || length > 8 * 1024 * 1024 || absolute < 0 || absolute > stream.Length - (long)length)
                    continue;
                previews.Add(new(absolute, (int)length, dim.Item1, dim.Item2));
            }
        }
        return (new(previews.OrderBy(p => p.Length).ToArray(), rotation, mirror), bytes);
    }
}
