using BitMiracle.LibTiff.Classic;

namespace FilmLight.Processing;

internal static class TiffGeometry
{
    internal static void Apply(string sourcePath, string destinationPath, NegativeFrameGeometry frame, CancellationToken ct)
    {
        using var source = Tiff.Open(sourcePath, "r") ?? throw new InvalidDataException("Cannot open positive master.");
        int width = source.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        int height = source.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        if (source.GetFieldDefaulted(TiffTag.BITSPERSAMPLE)[0].ToInt() != 16
            || source.GetFieldDefaulted(TiffTag.SAMPLESPERPIXEL)[0].ToInt() != 3
            || source.GetFieldDefaulted(TiffTag.PLANARCONFIG)[0].ToInt() != (int)PlanarConfig.CONTIG
            || source.GetFieldDefaulted(TiffTag.PHOTOMETRIC)[0].ToInt() != (int)Photometric.RGB
            || source.GetFieldDefaulted(TiffTag.SAMPLEFORMAT)[0].ToInt() != (int)SampleFormat.UINT
            || source.IsTiled())
            throw new InvalidDataException("Geometry requires a contiguous 16-bit RGB master.");
        var crop = frame.ToPhotoEditTransform(width, height).CropBounds;
        var pixels = new ushort[checked(crop.Width * crop.Height * 3)];
        var row = new byte[source.ScanlineSize()];
        for (int y = 0; y < crop.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            if (!source.ReadScanline(row, y + crop.Y)) throw new InvalidDataException("Cannot decode positive master row.");
            Buffer.BlockCopy(row, crop.X * 6, pixels, y * crop.Width * 6, crop.Width * 6);
        }
        double radians = frame.RotationDegrees * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        int outWidth = Math.Max(1, (int)Math.Ceiling(Math.Abs(crop.Width * cos) + Math.Abs(crop.Height * sin) - 1e-9));
        int outHeight = Math.Max(1, (int)Math.Ceiling(Math.Abs(crop.Width * sin) + Math.Abs(crop.Height * cos) - 1e-9));
        using var output = Tiff.Open(destinationPath, "w") ?? throw new IOException("Cannot create framed positive master.");
        output.SetField(TiffTag.IMAGEWIDTH, outWidth);
        output.SetField(TiffTag.IMAGELENGTH, outHeight);
        output.SetField(TiffTag.BITSPERSAMPLE, 16);
        output.SetField(TiffTag.SAMPLESPERPIXEL, 3);
        output.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        output.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        output.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.UINT);
        output.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        output.SetField(TiffTag.COMPRESSION, Compression.NONE);
        output.SetField(TiffTag.ROWSPERSTRIP, output.DefaultStripSize(0));
        var profile = source.GetField(TiffTag.ICCPROFILE)
            ?? throw new InvalidDataException("Positive master is missing its color profile.");
        output.SetField(TiffTag.ICCPROFILE, profile[0].ToInt(), profile[1].ToByteArray());
        row = new byte[checked(outWidth * 6)];
        for (int y = 0; y < outHeight; y++)
        {
            ct.ThrowIfCancellationRequested();
            Array.Clear(row);
            for (int x = 0; x < outWidth; x++)
            {
                double dx = (frame.FlipHorizontal ? outWidth - 1 - x : x) - (outWidth - 1) / 2d;
                double dy = (frame.FlipVertical ? outHeight - 1 - y : y) - (outHeight - 1) / 2d;
                double sx = cos * dx + sin * dy + (crop.Width - 1) / 2d;
                double sy = -sin * dx + cos * dy + (crop.Height - 1) / 2d;
                if (sx < -1e-8 || sy < -1e-8 || sx > crop.Width - 1 + 1e-8 || sy > crop.Height - 1 + 1e-8) continue;
                sx = Math.Clamp(sx, 0, crop.Width - 1); sy = Math.Clamp(sy, 0, crop.Height - 1);
                int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, crop.Width - 1), y1 = Math.Min(y0 + 1, crop.Height - 1);
                for (int c = 0; c < 3; c++)
                {
                    double a = pixels[(y0 * crop.Width + x0) * 3 + c] * (1 - (sx - x0))
                        + pixels[(y0 * crop.Width + x1) * 3 + c] * (sx - x0);
                    double b = pixels[(y1 * crop.Width + x0) * 3 + c] * (1 - (sx - x0))
                        + pixels[(y1 * crop.Width + x1) * 3 + c] * (sx - x0);
                    BitConverter.TryWriteBytes(row.AsSpan(x * 6 + c * 2), (ushort)Math.Round(a * (1 - (sy - y0)) + b * (sy - y0)));
                }
            }
            if (!output.WriteScanline(row, y)) throw new IOException("Cannot write framed positive master row.");
        }
        if (!output.WriteDirectory()) throw new IOException("Cannot finish framed positive master.");
    }
}
