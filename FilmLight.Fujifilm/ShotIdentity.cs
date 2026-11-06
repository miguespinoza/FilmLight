using System.Buffers.Binary;

namespace FilmLight.Fujifilm;

public static class ShotIdentity
{
    public static uint Read(ReadOnlySpan<byte> image, int format)
    {
        if (format == 1)
        {
            if (image.Length < 92 || !image.StartsWith("FUJIFILMCCD-RAW"u8))
                throw new InvalidDataException("Invalid RAF header.");
            var offset = CheckedOffset(BinaryPrimitives.ReadUInt32BigEndian(image.Slice(84, 4)));
            var length = CheckedOffset(BinaryPrimitives.ReadUInt32BigEndian(image.Slice(88, 4)));
            image = Slice(image, offset, length);
        }
        else if (format != 7)
            throw new InvalidDataException($"Unsupported still format {format}; select RAW/JPEG.");
        if (!image.StartsWith(new byte[] { 0xFF, 0xD8 }))
            throw new InvalidDataException("Invalid JPEG header.");
        var position = 2;
        while (position < image.Length)
        {
            if (image[position++] != 0xFF)
                throw new InvalidDataException("Invalid JPEG marker.");
            while (position < image.Length && image[position] == 0xFF) position++;
            if (position >= image.Length) break;
            var marker = image[position++];
            if (marker is 0xDA or 0xD9) break;
            if (marker is 0x01 or >= 0xD0 and <= 0xD7) continue;
            var size = BinaryPrimitives.ReadUInt16BigEndian(Slice(image, position, 2));
            if (size < 2) throw new InvalidDataException("Invalid JPEG segment length.");
            var segment = Slice(image, position + 2, size - 2);
            if (marker == 0xE1 && segment.StartsWith("Exif\0\0"u8))
                return ReadTiff(segment[6..]);
            position = checked(position + size);
        }
        throw new InvalidDataException("No Fujifilm shot identity found in EXIF.");
    }

    private static uint ReadTiff(ReadOnlySpan<byte> tiff)
    {
        if (!Slice(tiff, 0, 4).SequenceEqual(new byte[] { 0x49, 0x49, 42, 0 }))
            throw new InvalidDataException("Only little-endian EXIF is supported.");
        var firstIfd = CheckedOffset(U32(tiff, 4));
        var exifEntry = Entry(tiff, firstIfd, 0x8769);
        var exifIfd = CheckedOffset(U32(exifEntry, 8));
        var makerEntry = Entry(tiff, exifIfd, 0x927C);
        var makerLength = CheckedOffset(U32(makerEntry, 4));
        var makerOffset = CheckedOffset(U32(makerEntry, 8));
        var maker = Slice(tiff, makerOffset, makerLength);
        if (!Slice(maker, 0, 8).SequenceEqual("FUJIFILM"u8))
            throw new InvalidDataException("Unexpected MakerNote manufacturer.");
        var sequenceEntry = Entry(maker, CheckedOffset(U32(maker, 8)), 0x1438);
        if (U16(sequenceEntry, 2) != 4 || U32(sequenceEntry, 4) != 1)
            throw new InvalidDataException("Unexpected Fujifilm shot-number type.");
        return U32(sequenceEntry, 8);
    }

    private static ReadOnlySpan<byte> Entry(ReadOnlySpan<byte> data, int offset, ushort tag)
    {
        var count = U16(data, offset);
        Slice(data, offset + 2, checked(count * 12));
        for (var i = 0; i < count; i++)
        {
            var entry = Slice(data, checked(offset + 2 + i * 12), 12);
            if (U16(entry, 0) == tag) return entry;
        }
        throw new InvalidDataException($"EXIF tag 0x{tag:X} is missing.");
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(Slice(data, offset, 2));
    private static uint U32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Slice(data, offset, 4));
    private static int CheckedOffset(uint value) => value <= int.MaxValue
        ? (int)value : throw new InvalidDataException("EXIF offset exceeds supported size.");
    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, int offset, int length) =>
        offset >= 0 && length >= 0 && offset <= data.Length - length
            ? data.Slice(offset, length) : throw new InvalidDataException("Truncated image or invalid EXIF offset.");
}
