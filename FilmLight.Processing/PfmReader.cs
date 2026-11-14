using System.Buffers.Binary;
using System.Globalization;
using System.Text;


namespace FilmLight.Processing;

public static class PfmReader
{
    public static NegativeLinearImage Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static ValueTask<NegativeLinearImage> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var image = Read(path);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(image);
    }

    public static NegativeLinearImage Read(Stream stream)
    {
        var (width, height, pixels) = ReadPixels(stream, null, default);
        return new(width, height, pixels);
    }

    public static NegativeChannelImage ReadChannel(string path, RgbCaptureChannel channel, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        using var stream = File.OpenRead(path);
        var (width, height, pixels) = ReadPixels(stream, (int)channel, ct);
        return new(width, height, pixels);
    }

    public static PixelSize Validate(string path, CancellationToken ct = default)
    {
        using var stream = File.OpenRead(path);
        var (width, height, _) = ReadPixels(stream, -1, ct);
        return new(width, height);
    }

    private static (int Width, int Height, float[] Pixels) ReadPixels(Stream stream, int? selected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        string magic = HeaderLine(stream);
        if (magic is not ("PF" or "Pf")) throw new InvalidDataException("Not a PFM image.");
        string[] dimensions = HeaderLine(stream).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (dimensions.Length != 2 || !int.TryParse(dimensions[0], out int width)
            || !int.TryParse(dimensions[1], out int height) || width <= 0 || height <= 0)
            throw new InvalidDataException("Invalid PFM dimensions.");
        if (!double.TryParse(HeaderLine(stream), NumberStyles.Float, CultureInfo.InvariantCulture, out double scale)
            || !double.IsFinite(scale) || scale == 0)
            throw new InvalidDataException("Invalid PFM scale.");
        int channels = magic == "PF" ? 3 : 1;
        long count = (long)width * height * (selected is null ? 3 : selected < 0 ? 0 : 1);
        if (count > Array.MaxLength) throw new InvalidDataException("PFM image is too large.");
        long payloadBytes = (long)width * height * channels * sizeof(float);
        if (stream.CanSeek && stream.Length - stream.Position < payloadBytes)
            throw new EndOfStreamException("Incomplete PFM pixel data.");
        float[] pixels = new float[(int)count];
        byte[] row = new byte[checked(width * channels * sizeof(float))];
        for (int fileY = 0; fileY < height; fileY++)
        {
            ct.ThrowIfCancellationRequested();
            stream.ReadExactly(row);
            int destination = (height - fileY - 1) * width * (selected is null ? 3 : 1);
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    var bytes = row.AsSpan((x * channels + (channels == 1 ? 0 : c)) * 4, 4);
                    int bits = scale < 0 ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : BinaryPrimitives.ReadInt32BigEndian(bytes);
                    float value = (float)(BitConverter.Int32BitsToSingle(bits) * Math.Abs(scale));
                    if (!float.IsFinite(value)) throw new InvalidDataException("PFM contains non-finite samples.");
                    if (selected is null) pixels[destination + x * 3 + c] = value;
                    else if (selected == c) pixels[destination + x] = value;
                }
            }
        }
        return (width, height, pixels);
    }

    private static string HeaderLine(Stream stream)
    {
        while (true)
        {
            var line = new StringBuilder();
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) throw new EndOfStreamException("Incomplete PFM header.");
                if (b == '\n') break;
                if (line.Length > 4096) throw new InvalidDataException("PFM header line is too long.");
                line.Append((char)b);
            }
            string text = line.ToString().Trim();
            if (text.Length > 0 && !text.StartsWith('#')) return text;
        }
    }
}

