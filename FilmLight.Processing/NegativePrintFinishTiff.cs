using BitMiracle.LibTiff.Classic;


namespace FilmLight.Processing;

internal static class NegativePrintFinishTiff
{
    public static PixelSize Apply(string sourcePath, string outputPath, NegativePrintFinish finish, CancellationToken ct)
    {
        NegativeConversionValidation.Validate(finish);
        using var source = Tiff.Open(sourcePath, "r") ?? throw new InvalidDataException("Cannot open Print Finish TIFF.");
        int width = source.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        int height = source.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        if (source.GetFieldDefaulted(TiffTag.BITSPERSAMPLE)[0].ToInt() != 16
            || source.GetFieldDefaulted(TiffTag.SAMPLESPERPIXEL)[0].ToInt() != 3
            || source.GetFieldDefaulted(TiffTag.PLANARCONFIG)[0].ToInt() != (int)PlanarConfig.CONTIG
            || source.GetFieldDefaulted(TiffTag.PHOTOMETRIC)[0].ToInt() != (int)Photometric.RGB
            || source.GetFieldDefaulted(TiffTag.SAMPLEFORMAT)[0].ToInt() != (int)SampleFormat.UINT
            || source.IsTiled())
            throw new InvalidDataException("Print Finish requires a stripped, contiguous unsigned 16-bit sRGB TIFF.");
        using var output = Tiff.Open(outputPath, "w") ?? throw new IOException("Cannot create Print Finish TIFF.");
        output.SetField(TiffTag.IMAGEWIDTH, width);
        output.SetField(TiffTag.IMAGELENGTH, height);
        output.SetField(TiffTag.BITSPERSAMPLE, 16);
        output.SetField(TiffTag.SAMPLESPERPIXEL, 3);
        output.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        output.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        output.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.UINT);
        output.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        output.SetField(TiffTag.COMPRESSION, Compression.NONE);
        output.SetField(TiffTag.ROWSPERSTRIP, output.DefaultStripSize(0));
        if (source.GetField(TiffTag.ICCPROFILE) is { } profile)
            output.SetField(TiffTag.ICCPROFILE, profile[0].ToInt(), profile[1].ToByteArray());
        var row = new byte[source.ScanlineSize()];
        var tone = new NegativePrintFinishMath.Prepared(finish);
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            if (!source.ReadScanline(row, y)) throw new InvalidDataException($"Cannot decode Print Finish TIFF row {y}.");
            for (int x = 0; x < width; x++)
            {
                int offset = x * 6;
                var rgb = tone.Apply(new(Decode(BitConverter.ToUInt16(row, offset) / 65535d),
                    Decode(BitConverter.ToUInt16(row, offset + 2) / 65535d),
                    Decode(BitConverter.ToUInt16(row, offset + 4) / 65535d)));
                Write(row, offset, rgb.R);
                Write(row, offset + 2, rgb.G);
                Write(row, offset + 4, rgb.B);
            }
            if (!output.WriteScanline(row, y)) throw new IOException($"Cannot write Print Finish TIFF row {y}.");
        }
        if (!output.WriteDirectory()) throw new IOException("Cannot complete Print Finish TIFF.");
        return new(width, height);
    }

    private static double Decode(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    private static void Write(byte[] row, int offset, double value)
    {
        double encoded = value <= .0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - .055;
        ushort sample = (ushort)Math.Clamp((int)Math.Round(encoded * 65535), 0, 65535);
        BitConverter.TryWriteBytes(row.AsSpan(offset, 2), sample);
    }
}

