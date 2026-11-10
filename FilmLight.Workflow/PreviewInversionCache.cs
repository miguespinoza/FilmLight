using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FilmLight.Workflow;

internal sealed class PreviewInversionCache(string directory)
{
    internal static string Key(LinearImage image, byte[] xmp, string cliPath, string version)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> dimensions = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(dimensions, image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(dimensions[4..], image.Height);
        hash.AppendData(dimensions);
        hash.AppendData(MemoryMarshal.AsBytes(image.Pixels.AsSpan()));
        hash.AppendData(xmp);
        hash.AppendData(ProcessingImages.LinearRec2020Profile());
        hash.AppendData(Encoding.UTF8.GetBytes("\0" + Path.GetFullPath(cliPath) + "\0" + version + "\0" +
            DarktableConversion.InversionSettings));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal bool TryRead(string key, int width, int height, CancellationToken token, out LinearImage? image)
    {
        image = null;
        string path = Path.Combine(directory, key + ".pfm");
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            string Line()
            {
                var line = new StringBuilder();
                int c;
                while ((c = stream.ReadByte()) != '\n')
                {
                    if (c < 0 || line.Length >= 100) throw new InvalidDataException("Invalid inversion-cache PFM header.");
                    line.Append((char)c);
                }
                return line.ToString();
            }
            if (Line() != "PF" || Line() != $"{width} {height}" || Line() != "-1.0" ||
                stream.Length - stream.Position != (long)width * height * 3 * sizeof(float))
                throw new InvalidDataException("Invalid inversion-cache PFM geometry or payload.");
            var pixels = new float[checked(width * height * 3)];
            for (int y = height - 1; y >= 0; y--)
            {
                token.ThrowIfCancellationRequested();
                var row = pixels.AsSpan(y * width * 3, width * 3);
                stream.ReadExactly(MemoryMarshal.AsBytes(row));
                foreach (float sample in row)
                    if (!float.IsFinite(sample)) throw new InvalidDataException("Non-finite inversion-cache sample.");
            }
            image = new(width, height, pixels);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (InvalidDataException error)
        {
            System.Diagnostics.Trace.TraceWarning($"Ignoring corrupt preview inversion cache {path}: {error.Message}");
            return false;
        }
        catch (EndOfStreamException error)
        {
            System.Diagnostics.Trace.TraceWarning($"Ignoring truncated preview inversion cache {path}: {error.Message}");
            return false;
        }
        catch (FileNotFoundException) { return false; }
        return true;
    }

    internal void Write(string key, LinearImage image, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, key + ".pfm");
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.ASCII.GetBytes(FormattableString.Invariant($"PF\n{image.Width} {image.Height}\n-1.0\n")));
                for (int y = image.Height - 1; y >= 0; y--)
                {
                    token.ThrowIfCancellationRequested();
                    stream.Write(MemoryMarshal.AsBytes(image.Pixels.AsSpan(y * image.Width * 3, image.Width * 3)));
                }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            foreach (var old in new DirectoryInfo(directory).GetFiles("*.pfm")
                .OrderByDescending(f => f.LastWriteTimeUtc).Skip(8))
                old.Delete();
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
