using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BitMiracle.LibTiff.Classic;

namespace FilmLight.Workflow;

internal sealed record LinearImage(int Width, int Height, float[] Pixels);

internal static partial class ProcessingImages
{
    internal const string SyntheticLabel = "SYNTHETIC Film Light - not a camera capture";
    internal const string LinearLabel = "FilmLight linear Rec2020; black-subtracted; exposure-locked";

    internal static LinearImage ReadLinear(string path, CancellationToken token, bool requireQualification = false)
    {
        using var tif = Tiff.Open(path, "r") ?? throw new InvalidDataException($"Cannot decode TIFF: {path}");
        int width = Required(tif, TiffTag.IMAGEWIDTH), height = Required(tif, TiffTag.IMAGELENGTH);
        int bits = Required(tif, TiffTag.BITSPERSAMPLE);
        int format = tif.GetFieldDefaulted(TiffTag.SAMPLEFORMAT)[0].ToInt();
        if (width <= 0 || height <= 0 || (long)width * height > 60_000_000 ||
            Required(tif, TiffTag.SAMPLESPERPIXEL) != 3 ||
            Required(tif, TiffTag.PHOTOMETRIC) != (int)Photometric.RGB ||
            Required(tif, TiffTag.PLANARCONFIG) != (int)PlanarConfig.CONTIG ||
            (tif.GetFieldDefaulted(TiffTag.ORIENTATION)[0].ToInt() != (int)Orientation.TOPLEFT) ||
            !((bits == 16 && format == (int)SampleFormat.UINT) ||
              (bits == 32 && format == (int)SampleFormat.IEEEFP)))
            throw new InvalidDataException($"Expected top-left, contiguous linear RGB 16-bit integer or 32-bit float TIFF: {path}");
        string description = tif.GetField(TiffTag.IMAGEDESCRIPTION)?[0].ToString() ?? "";
        if (requireQualification && !description.Contains(LinearLabel, StringComparison.Ordinal))
            throw new InvalidDataException($"Unqualified linear input: {path}. Supply a black-subtracted, registered linear Rec2020 TIFF at locked exposure, tagged '{LinearLabel}'. RAF (including lossy X-T5 RAF) and camera JPEG are not qualified by this processor.");
        if (requireQualification) ValidateLinearProfile(tif, path);
        if (tif.IsTiled())
            throw new InvalidDataException($"Tiled input is not supported; supply a scanline TIFF: {path}");
        var pixels = new float[checked(width * height * 3)];
        var row = new byte[tif.ScanlineSize()];
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            if (!tif.ReadScanline(row, y)) throw new InvalidDataException($"Truncated TIFF at row {y}: {path}");
            if (bits == 32)
            {
                var samples = MemoryMarshal.Cast<byte, float>(row.AsSpan(0, width * 3 * 4));
                ValidateSamples(samples, $"Invalid linear sample in {path}, row {y}.");
                samples.CopyTo(pixels.AsSpan(y * width * 3, width * 3));
                continue;
            }
            for (int i = 0; i < width * 3; i++)
            {
                float value = BitConverter.ToUInt16(row, i * 2) / 65535f;
                // Camera-profile matrices and black-subtracted noise legitimately produce signed RGB.
                if (!float.IsFinite(value))
                    throw new InvalidDataException($"Invalid linear sample in {path}, row {y}.");
                pixels[y * width * 3 + i] = value;
            }
        }
        return new(width, height, pixels);
    }

    internal static void WriteTiff(string path, LinearImage image, bool floating, string description,
        CancellationToken token, byte[]? profile = null)
    {
        using var tif = Tiff.Open(path, "w") ?? throw new IOException($"Cannot create {path}");
        tif.SetField(TiffTag.IMAGEWIDTH, image.Width);
        tif.SetField(TiffTag.IMAGELENGTH, image.Height);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, 3);
        tif.SetField(TiffTag.BITSPERSAMPLE, floating ? 32 : 16);
        tif.SetField(TiffTag.SAMPLEFORMAT, floating ? SampleFormat.IEEEFP : SampleFormat.UINT);
        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tif.SetField(TiffTag.COMPRESSION, Compression.NONE);
        tif.SetField(TiffTag.ROWSPERSTRIP, 16);
        tif.SetField(TiffTag.IMAGEDESCRIPTION, description);
        if (profile is not null) tif.SetField(TiffTag.ICCPROFILE, profile.Length, profile);
        var row = new byte[image.Width * 3 * (floating ? 4 : 2)];
        for (int y = 0; y < image.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            var samples = image.Pixels.AsSpan(y * image.Width * 3, image.Width * 3);
            ValidateSamples(samples, "Non-finite image sample.");
            if (floating) MemoryMarshal.AsBytes(samples).CopyTo(row);
            else for (int x = 0; x < image.Width * 3; x++)
            {
                BitConverter.TryWriteBytes(row.AsSpan(x * 2, 2), (ushort)Math.Round(Math.Clamp(samples[x], 0, 1) * 65535));
            }
            if (!tif.WriteScanline(row, y)) throw new IOException($"Cannot write TIFF row {y}: {path}");
        }
        if (!tif.WriteDirectory()) throw new IOException($"Cannot finish TIFF: {path}");
    }

    private static void ValidateSamples(ReadOnlySpan<float> samples, string message)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var limit = new Vector<float>(float.MaxValue);
            for (; i <= samples.Length - Vector<float>.Count; i += Vector<float>.Count)
                if (!Vector.LessThanOrEqualAll(Vector.Abs(new Vector<float>(samples.Slice(i, Vector<float>.Count))), limit))
                    throw new InvalidDataException(message);
        }
        for (; i < samples.Length; i++)
            if (!float.IsFinite(samples[i])) throw new InvalidDataException(message);
    }

    internal static void ValidateMaster(string path, int width, int height, bool requireProfile, CancellationToken token)
    {
        using (var tif = Tiff.Open(path, "r") ?? throw new InvalidDataException($"Missing TIFF: {path}"))
        {
            if (Required(tif, TiffTag.BITSPERSAMPLE) != 16 ||
                tif.GetFieldDefaulted(TiffTag.SAMPLEFORMAT)[0].ToInt() != (int)SampleFormat.UINT)
                throw new InvalidDataException("Export is not a 16-bit integer TIFF.");
            var profile = tif.GetField(TiffTag.ICCPROFILE);
            if (requireProfile && (profile is null || profile[0].ToInt() < 128))
                throw new InvalidDataException("TIFF export has no embedded ICC profile.");
        }
        var decoded = ReadLinear(path, token);
        if (decoded.Width != width || decoded.Height != height)
            throw new InvalidDataException("TIFF dimensions do not match the recipe crop.");
    }

    internal static LinearImage CropForPreview(LinearImage image, DevelopRecipe recipe, CancellationToken token)
    {
        var (left, top, width, height) = ReferenceReconstruction.Crop(image, recipe);
        double scale = Math.Min(1, 1600.0 / Math.Max(width, height));
        int w = Math.Max(1, (int)(width * scale)), h = Math.Max(1, (int)(height * scale));
        var small = new LinearImage(w, h, new float[w * h * 3]);
        for (int y = 0; y < h; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int i = ((top + Math.Min(height - 1, (int)(y / scale))) * image.Width
                    + left + Math.Min(width - 1, (int)(x / scale))) * 3;
                Array.Copy(image.Pixels, i, small.Pixels, (y * w + x) * 3, 3);
            }
        }
        return small;
    }

    [SupportedOSPlatform("windows")]
    internal static void WriteSourcePreview(string path, LinearImage image, DevelopRecipe recipe, CancellationToken token)
    {
        var small = CropForPreview(image, recipe, token);
        for (int i = 0; i < small.Pixels.Length; i += 3)
        {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            var (r, g, b) = ToLinearSrgb(small.Pixels[i], small.Pixels[i + 1], small.Pixels[i + 2]);
            small.Pixels[i] = r;
            small.Pixels[i + 1] = g;
            small.Pixels[i + 2] = b;
        }
        WriteJpeg(path, small, token);
    }

    [SupportedOSPlatform("windows")]
    internal static void WriteJpeg(string path, LinearImage image, CancellationToken token)
    {
        using var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, image.Width, image.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            for (int y = 0; y < image.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < image.Width; x++)
                for (int c = 0; c < 3; c++)
                {
                    double v = Math.Clamp(image.Pixels[(y * image.Width + x) * 3 + c], 0, 1);
                    double encoded = v <= .0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - .055;
                    row[x * 3 + 2 - c] = (byte)Math.Round(encoded * 255);
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { bitmap.UnlockBits(data); }
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        bitmap.Save(output, ImageFormat.Jpeg);
    }

    [SupportedOSPlatform("windows")]
    internal static void ValidateJpeg(string path, int width, int height, bool requireProfile)
    {
        using var stream = File.OpenRead(path);
        using var image = Image.FromStream(stream, false, true);
        if (image.RawFormat.Guid != ImageFormat.Jpeg.Guid || image.Width != width || image.Height != height)
            throw new InvalidDataException("JPEG failed format/dimension validation.");
        using var decoded = new Bitmap(image);
        _ = decoded.GetPixel(width - 1, height - 1);
        if (requireProfile && (!image.PropertyIdList.Contains(0x8773) ||
            (image.GetPropertyItem(0x8773)?.Len ?? 0) < 128))
            throw new InvalidDataException("JPEG export has no embedded ICC profile.");
    }

    private static int Required(Tiff tif, TiffTag tag) =>
        tif.GetField(tag)?[0].ToInt() ?? throw new InvalidDataException($"Missing TIFF tag {tag}.");

    private static void ValidateLinearProfile(Tiff tif, string path)
    {
        var field = tif.GetField(TiffTag.ICCPROFILE);
        byte[] data = field?[1].ToByteArray() ?? [];
        bool valid = false;
        if (data.Length >= 132 && data.AsSpan(36, 4).SequenceEqual("acsp"u8) &&
            data.AsSpan(16, 4).SequenceEqual("RGB "u8) && data.AsSpan(20, 4).SequenceEqual("XYZ "u8))
        {
            uint count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(128));
            if (count <= (data.Length - 132) / 12)
            {
                var tags = new Dictionary<string, byte[]>();
                for (int i = 0; i < count; i++)
                {
                    var entry = data.AsSpan(132 + i * 12, 12);
                    uint offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
                    uint size = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
                    if (offset > data.Length || size > data.Length - offset) continue;
                    tags[System.Text.Encoding.ASCII.GetString(entry[..4])] = data.AsSpan((int)offset, (int)size).ToArray();
                }
                double[][] matrix = [[.67346, .27903, -.00193], [.16566, .67534, .02998], [.12510, .04563, .79716]];
                valid = true;
                for (int c = 0; c < 3; c++)
                {
                    string channel = "rgb"[c].ToString();
                    if (!tags.TryGetValue(channel + "XYZ", out var xyz) || xyz.Length < 20 ||
                        !xyz.AsSpan(0, 4).SequenceEqual("XYZ "u8))
                    { valid = false; break; }
                    for (int j = 0; j < 3; j++)
                        valid &= Math.Abs(BinaryPrimitives.ReadInt32BigEndian(xyz.AsSpan(8 + j * 4)) / 65536.0 - matrix[c][j]) < .003;
                    if (!tags.TryGetValue(channel + "TRC", out var trc) || trc.Length < 12)
                    { valid = false; break; }
                    bool curve = trc.AsSpan(0, 4).SequenceEqual("curv"u8) &&
                        (BinaryPrimitives.ReadUInt32BigEndian(trc.AsSpan(8)) == 0 ||
                        (trc.Length >= 14 && BinaryPrimitives.ReadUInt32BigEndian(trc.AsSpan(8)) == 1 &&
                        BinaryPrimitives.ReadUInt16BigEndian(trc.AsSpan(12)) == 256));
                    bool parametric = trc.Length >= 16 && trc.AsSpan(0, 4).SequenceEqual("para"u8) &&
                        BinaryPrimitives.ReadUInt16BigEndian(trc.AsSpan(8)) == 0 &&
                        BinaryPrimitives.ReadInt32BigEndian(trc.AsSpan(12)) == 65536;
                    valid &= curve || parametric;
                }
            }
        }
        if (!valid)
            throw new InvalidDataException($"Input must embed a linear Rec2020 matrix ICC profile; unprofiled, gamma-encoded or other-primary input cannot be normalized safely: {path}");
    }

    // ICC v2 matrix/shaper profile, D50-adapted Rec2020 primaries and linear TRCs.
    internal static byte[] LinearRec2020Profile()
    {
        static byte[] Xyz(double x, double y, double z)
        {
            var b = new byte[20]; "XYZ "u8.CopyTo(b);
            for (int i = 0; i < 3; i++)
                BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8 + i * 4), (int)Math.Round(new[] { x, y, z }[i] * 65536));
            return b;
        }
        byte[] curve = new byte[16]; "curv"u8.CopyTo(curve);
        BinaryPrimitives.WriteUInt32BigEndian(curve.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(12), 256);
        byte[] desc = new byte[128]; "desc"u8.CopyTo(desc);
        var name = System.Text.Encoding.ASCII.GetBytes("FilmLight linear Rec2020\0");
        BinaryPrimitives.WriteInt32BigEndian(desc.AsSpan(8), name.Length); name.CopyTo(desc, 12);
        (string Tag, byte[] Data)[] tags =
        [
            ("desc", desc), ("wtpt", Xyz(.96422, 1, .82521)),
            ("rXYZ", Xyz(.67346, .27903, -.00193)), ("gXYZ", Xyz(.16566, .67534, .02998)),
            ("bXYZ", Xyz(.12510, .04563, .79716)), ("rTRC", curve), ("gTRC", curve), ("bTRC", curve)
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
            System.Text.Encoding.ASCII.GetBytes(tags[i].Tag).CopyTo(entry);
            BinaryPrimitives.WriteInt32BigEndian(entry[4..], offset);
            BinaryPrimitives.WriteInt32BigEndian(entry[8..], tags[i].Data.Length);
            tags[i].Data.CopyTo(result, offset);
            offset += (tags[i].Data.Length + 3) & ~3;
        }
        return result;
    }
}
