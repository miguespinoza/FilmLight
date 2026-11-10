using System.Runtime.Versioning;
using BitMiracle.LibTiff.Classic;

namespace FilmLight.Workflow;

internal static partial class ProcessingImages
{
    internal static void WriteProPhotoTiff(string path, LinearImage image, bool synthetic, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var tif = Tiff.Open(path, "w") ?? throw new IOException($"Cannot create {path}");
        tif.SetField(TiffTag.IMAGEWIDTH, image.Width);
        tif.SetField(TiffTag.IMAGELENGTH, image.Height);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, 3);
        tif.SetField(TiffTag.BITSPERSAMPLE, 16);
        tif.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.UINT);
        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tif.SetField(TiffTag.COMPRESSION, Compression.NONE);
        tif.SetField(TiffTag.ROWSPERSTRIP, 16);
        tif.SetField(TiffTag.IMAGEDESCRIPTION, (synthetic ? SyntheticLabel + "; " : "") + "FilmLight Linear ProPhoto RGB");
        byte[] profile = ProPhotoProfile();
        tif.SetField(TiffTag.ICCPROFILE, profile.Length, profile);
        var row = new byte[checked(image.Width * 6)];
        for (int y = 0; y < image.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            var samples = image.Pixels.AsSpan(y * image.Width * 3, image.Width * 3);
            ValidateSamples(samples, "Non-finite image sample.");
            for (int x = 0; x < image.Width; x++)
            {
                var (r, g, b) = ToLinearProPhoto(samples[x * 3], samples[x * 3 + 1], samples[x * 3 + 2]);
                static ushort Quantize(float value) => (ushort)Math.Round(Math.Clamp(value, 0, 1) * 65535);
                BitConverter.TryWriteBytes(row.AsSpan(x * 6), Quantize(r));
                BitConverter.TryWriteBytes(row.AsSpan(x * 6 + 2), Quantize(g));
                BitConverter.TryWriteBytes(row.AsSpan(x * 6 + 4), Quantize(b));
            }
            if (!tif.WriteScanline(row, y)) throw new IOException($"Cannot write TIFF row {y}: {path}");
        }
        if (!tif.WriteDirectory()) throw new IOException($"Cannot finish TIFF: {path}");
    }

    [SupportedOSPlatform("windows")]
    internal static void WriteExportJpeg(string path, LinearImage image, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var encoder = new WicJpegEncoder(path, image.Width, image.Height, SrgbProfile());
        var row = new byte[checked(image.Width * 3)];
        for (int y = 0; y < image.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            var samples = image.Pixels.AsSpan(y * image.Width * 3, image.Width * 3);
            ValidateSamples(samples, "Non-finite image sample.");
            for (int x = 0; x < image.Width; x++)
            {
                var (r, g, b) = ToLinearSrgb(samples[x * 3], samples[x * 3 + 1], samples[x * 3 + 2]);
                row[x * 3] = EncodeSrgb(b); row[x * 3 + 1] = EncodeSrgb(g); row[x * 3 + 2] = EncodeSrgb(r);
            }
            encoder.WriteRow(row);
        }
        token.ThrowIfCancellationRequested();
        encoder.Commit();
    }
}
