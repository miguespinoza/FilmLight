using System.Buffers.Binary;

namespace FilmLight.Fujifilm;

public static class CapturePreview
{
    public static async Task<Stream> OpenAsync(string path)
    {
        if (Path.GetExtension(path).Equals(".JPG", StringComparison.OrdinalIgnoreCase))
            return File.OpenRead(path);
        if (!Path.GetExtension(path).Equals(".RAF", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only JPEG and RAF capture previews are supported.");

        await using var file = File.OpenRead(path);
        var header = new byte[92];
        await file.ReadExactlyAsync(header);
        if (!header.AsSpan().StartsWith("FUJIFILMCCD-RAW"u8))
            throw new InvalidDataException("Invalid RAF preview header.");
        var offset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(84, 4));
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(88, 4));
        if (offset < header.Length || length < 2 || length > 64 * 1024 * 1024
            || (long)offset + length > file.Length)
            throw new InvalidDataException("Invalid RAF preview range.");
        file.Position = offset;
        var jpeg = new byte[checked((int)length)];
        await file.ReadExactlyAsync(jpeg);
        if (jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new InvalidDataException("RAF preview is not a JPEG image.");
        return new MemoryStream(jpeg, writable: false);
    }
}
