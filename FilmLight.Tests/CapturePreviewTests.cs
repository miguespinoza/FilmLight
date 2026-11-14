using System.Buffers.Binary;
using FilmLight.Fujifilm;
using Xunit;

namespace FilmLight.Tests;

public sealed class CapturePreviewTests
{
    [Fact]
    public async Task RafUsesEmbeddedJpegWithoutReturningRawSensorData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"FilmLight-preview-{Guid.NewGuid():N}.RAF");
        var image = new byte[160];
        "FUJIFILMCCD-RAW"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(84, 4), 100);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(88, 4), 4);
        new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }.CopyTo(image, 100);
        await File.WriteAllBytesAsync(path, image);
        try
        {
            await using var stream = await CapturePreview.OpenAsync(path);
            var preview = new byte[4];
            await stream.ReadExactlyAsync(preview);
            Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, preview);
            Assert.Equal(4, stream.Length);
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(88, 4), 1000);
            await File.WriteAllBytesAsync(path, image);
            await Assert.ThrowsAsync<InvalidDataException>(() => CapturePreview.OpenAsync(path));
        }
        finally { File.Delete(path); }
    }
}
