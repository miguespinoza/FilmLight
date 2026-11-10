using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;

namespace FilmLight.Workflow;

internal static partial class ProcessingImages
{
    internal static void WritePreviewPng(string path, LinearImage image, CancellationToken token)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8; header[9] = 2;
        WritePngChunk(output, "IHDR", header);
        using (var profile = new MemoryStream())
        {
            profile.Write("FilmLight sRGB\0\0"u8);
            using (var zlib = new ZLibStream(profile, CompressionLevel.Fastest, true))
                zlib.Write(SrgbProfile());
            WritePngChunk(output, "iCCP", profile.ToArray());
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            var row = new byte[1 + image.Width * 3];
            for (int y = 0; y < image.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < image.Width; x++)
                {
                    int i = (y * image.Width + x) * 3, j = 1 + x * 3;
                    var (r, g, b) = ToLinearSrgb(image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
                    row[j] = EncodeSrgb(r); row[j + 1] = EncodeSrgb(g); row[j + 2] = EncodeSrgb(b);
                }
                zlib.Write(row);
            }
        }
        WritePngChunk(output, "IDAT", compressed.ToArray());
        WritePngChunk(output, "IEND", []);
    }

    private static readonly uint[] PngCrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        uint crc = (uint)i;
        for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        return crc;
    }).ToArray();

    private static uint PngCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in type) crc = PngCrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        foreach (byte b in data) crc = PngCrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        return ~crc;
    }

    private static void WritePngChunk(Stream stream, string name, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length); stream.Write(word);
        byte[] type = Encoding.ASCII.GetBytes(name);
        stream.Write(type); stream.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(word, PngCrc(type, data)); stream.Write(word);
    }

    [SupportedOSPlatform("windows")]
    internal static void ValidatePng(string path, int width, int height, bool requireProfile)
    {
        using var stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[8];
        stream.ReadExactly(signature);
        if (!signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("PNG failed signature validation.");
        bool header = false, profile = false, end = false;
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> crc = stackalloc byte[4];
        while (stream.Position < stream.Length)
        {
            stream.ReadExactly(chunk);
            int length = BinaryPrimitives.ReadInt32BigEndian(chunk);
            if (length < 0 || length > stream.Length - stream.Position - 4)
                throw new InvalidDataException("Truncated PNG chunk.");
            var data = new byte[length];
            stream.ReadExactly(data); stream.ReadExactly(crc);
            if (BinaryPrimitives.ReadUInt32BigEndian(crc) != PngCrc(chunk[4..], data))
                throw new InvalidDataException("PNG failed CRC validation.");
            if (!header)
            {
                if (!chunk[4..].SequenceEqual("IHDR"u8) || length != 13 ||
                    BinaryPrimitives.ReadInt32BigEndian(data) != width ||
                    BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4)) != height || data[8] != 8 || data[9] != 2)
                    throw new InvalidDataException("PNG failed format/dimension validation.");
                header = true;
            }
            if (chunk[4..].SequenceEqual("iCCP"u8)) profile = length > 16;
            if (chunk[4..].SequenceEqual("IEND"u8)) { end = length == 0 && stream.Position == stream.Length; break; }
        }
        if (!header || !end || (requireProfile && !profile))
            throw new InvalidDataException("PNG is incomplete or has no embedded color profile.");
        stream.Position = 0;
        using var image = Image.FromStream(stream, false, true);
        if (image.RawFormat.Guid != ImageFormat.Png.Guid || image.Width != width || image.Height != height)
            throw new InvalidDataException("PNG failed decoding validation.");
        using var decoded = new Bitmap(image);
        _ = decoded.GetPixel(width - 1, height - 1);
    }

    internal static byte[] SrgbProfile()
    {
        static byte[] Xyz(double x, double y, double z)
        {
            var b = new byte[20]; "XYZ "u8.CopyTo(b);
            double[] values = [x, y, z];
            for (int i = 0; i < 3; i++)
                BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8 + i * 4), (int)Math.Round(values[i] * 65536));
            return b;
        }
        // ICC v2 represents the piecewise sRGB decoding curve with a sampled shaper.
        // Parametric 'para' tags require v4 and are not universally understood by Windows decoders.
        byte[] curve = new byte[12 + 4096 * 2]; "curv"u8.CopyTo(curve);
        BinaryPrimitives.WriteInt32BigEndian(curve.AsSpan(8), 4096);
        for (int i = 0; i < 4096; i++)
        {
            double v = i / 4095.0;
            double linear = v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4);
            BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(12 + i * 2), (ushort)Math.Round(linear * 65535));
        }
        byte[] desc = new byte[128]; "desc"u8.CopyTo(desc);
        var name = Encoding.ASCII.GetBytes("FilmLight sRGB\0");
        BinaryPrimitives.WriteInt32BigEndian(desc.AsSpan(8), name.Length); name.CopyTo(desc, 12);
        byte[] copyright = Encoding.ASCII.GetBytes("text\0\0\0\0Public domain sRGB colorimetry\0");
        (string Tag, byte[] Data)[] tags =
        [
            ("desc", desc), ("cprt", copyright), ("wtpt", Xyz(.96422, 1, .82521)),
            ("rXYZ", Xyz(.4360747, .2225045, .0139322)), ("gXYZ", Xyz(.3850649, .7168786, .0971045)),
            ("bXYZ", Xyz(.1430804, .0606169, .7141733)), ("rTRC", curve), ("gTRC", curve), ("bTRC", curve)
        ];
        int offset = 132 + tags.Length * 12;
        var result = new byte[offset + tags.Sum(t => (t.Data.Length + 3) & ~3)];
        BinaryPrimitives.WriteInt32BigEndian(result, result.Length);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), 0x02100000);
        "mntr"u8.CopyTo(result.AsSpan(12)); "RGB "u8.CopyTo(result.AsSpan(16));
        "XYZ "u8.CopyTo(result.AsSpan(20)); "acsp"u8.CopyTo(result.AsSpan(36));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(24), 2026);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(28), 1);
        Xyz(.96422, 1, .82521).AsSpan(8, 12).CopyTo(result.AsSpan(68));
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(128), tags.Length);
        for (int i = 0; i < tags.Length; i++)
        {
            var entry = result.AsSpan(132 + i * 12, 12);
            Encoding.ASCII.GetBytes(tags[i].Tag).CopyTo(entry);
            BinaryPrimitives.WriteInt32BigEndian(entry[4..], offset);
            BinaryPrimitives.WriteInt32BigEndian(entry[8..], tags[i].Data.Length);
            tags[i].Data.CopyTo(result, offset);
            offset += (tags[i].Data.Length + 3) & ~3;
        }
        return result;
    }
}
