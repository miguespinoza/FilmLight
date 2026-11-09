using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using FilmLight.Workflow;

[assembly: SupportedOSPlatform("windows")]

internal sealed record DemoResult(string Input, string Output, string Status, string? Error,
    int Width, int Height, SprocketLocations? Points);

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: FilmLight.CalibrationDemo <input-directory> <output-directory>");
            return 1;
        }
        string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
        if (!Directory.Exists(input)) throw new DirectoryNotFoundException(input);
        if (input.Equals(output, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a separate output directory; originals are never overwritten.");
        Directory.CreateDirectory(output);
        string[] files = Directory.EnumerateFiles(input)
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".raf" or ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff")
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("No supported images found.");
        var results = new List<DemoResult>();
        foreach (string file in files)
        {
            string name = Path.GetFileName(file), imageName = name + ".annotated.png";
            try
            {
                using var original = ReadPreview(file);
                double scale = Math.Min(1, 1800.0 / Math.Max(original.Width, original.Height));
                using var canvas = new Bitmap(Math.Max(1, (int)(original.Width * scale)),
                    Math.Max(1, (int)(original.Height * scale)), PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(canvas))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(original, 0, 0, canvas.Width, canvas.Height);
                }
                SprocketLocations? points = null;
                string? error = null;
                try { points = SprocketCalibration.Locate(ToLinear(canvas), default); }
                catch (InvalidDataException ex) { error = ex.Message; }
                using (var graphics = Graphics.FromImage(canvas))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    if (points is not null)
                    {
                        Dot(graphics, canvas, points.LightX, points.LightY, Color.Red);
                        Dot(graphics, canvas, points.FilmBaseX, points.FilmBaseY, Color.Blue);
                    }
                    else
                    {
                        using var brush = new SolidBrush(Color.FromArgb(220, 15, 17, 22));
                        using var font = new Font("Segoe UI", 18, FontStyle.Bold);
                        graphics.FillRectangle(brush, 0, 0, canvas.Width, 44);
                        graphics.DrawString("NO RELIABLE SPROCKET / FILM-BASE PAIR", font, Brushes.Orange, 12, 8);
                    }
                }
                canvas.Save(Path.Combine(output, imageName), ImageFormat.Png);
                results.Add(new(name, imageName, points is null ? "not-detected" : "detected", error,
                    original.Width, original.Height, points));
                Console.WriteLine($"{name}: {(points is null ? "NOT DETECTED - " + error : $"light ({points.LightX:F4}, {points.LightY:F4}), film ({points.FilmBaseX:F4}, {points.FilmBaseY:F4})")}");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or ExternalException)
            {
                Console.Error.WriteLine($"{name}: ERROR - {ex.Message}");
                results.Add(new(name, "", "error", ex.Message, 0, 0, null));
            }
        }
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            Method = "Shared FilmLight sprocket geometry detector on display previews; NOT linear RAW color calibration.",
            Legend = "RED: bare backlight through a sprocket hole. BLUE: clear film between adjacent holes.",
            Images = results
        }, new JsonSerializerOptions { WriteIndented = true }));
        WriteGallery(output, results);
        Console.WriteLine($"Annotated {results.Count(r => r.Output.Length > 0)}/{files.Length}; paired samples {results.Count(r => r.Points is not null)}/{files.Length}. {Path.Combine(output, "index.html")}");
        return results.Any(r => r.Status == "error") ? 1 : 0;
    }

    private static Bitmap ReadPreview(string path)
    {
        if (!Path.GetExtension(path).Equals(".raf", StringComparison.OrdinalIgnoreCase))
        {
            using var image = Image.FromFile(path);
            return new(image);
        }
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[96];
        file.ReadExactly(header);
        if (!header[..16].SequenceEqual("FUJIFILMCCD-RAW "u8))
            throw new InvalidDataException("Not a Fujifilm RAF header.");
        uint offset = BinaryPrimitives.ReadUInt32BigEndian(header[84..]);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header[88..]);
        if (length < 100 || length > 100_000_000 || offset > file.Length || length > file.Length - offset)
            throw new InvalidDataException("Invalid RAF embedded JPEG bounds.");
        file.Position = offset;
        var bytes = new byte[length];
        file.ReadExactly(bytes);
        if (bytes[0] != 255 || bytes[1] != 216) throw new InvalidDataException("RAF preview is not JPEG.");
        using var stream = new MemoryStream(bytes, writable: false);
        using var preview = Image.FromStream(stream);
        return new(preview);
    }

    private static LinearImage ToLinear(Bitmap bitmap)
    {
        var pixels = new float[bitmap.Width * bitmap.Height * 3];
        var data = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            float[] linear = Enumerable.Range(0, 256).Select(v =>
            {
                double s = v / 255.0;
                return (float)(s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4));
            }).ToArray();
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < bitmap.Width; x++)
                {
                    float r = linear[row[x * 3 + 2]], g = linear[row[x * 3 + 1]], b = linear[row[x * 3]];
                    int i = (y * bitmap.Width + x) * 3;
                    pixels[i] = .627404f * r + .329283f * g + .043313f * b;
                    pixels[i + 1] = .069097f * r + .919540f * g + .011362f * b;
                    pixels[i + 2] = .016391f * r + .088013f * g + .895595f * b;
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
        return new(bitmap.Width, bitmap.Height, pixels);
    }

    private static void Dot(Graphics graphics, Bitmap image, double x, double y, Color color)
    {
        float radius = Math.Max(6, image.Width / 160f);
        var bounds = new RectangleF((float)(x * image.Width) - radius, (float)(y * image.Height) - radius, radius * 2, radius * 2);
        using var brush = new SolidBrush(color);
        using var pen = new Pen(Color.White, 2);
        graphics.FillEllipse(brush, bounds);
        graphics.DrawEllipse(pen, bounds);
    }

    private static void WriteGallery(string output, List<DemoResult> results)
    {
        int detected = results.Count(r => r.Points is not null);
        var html = new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>FilmLight sprocket demo</title><style>body{margin:0;padding:24px;background:#11141a;color:#eee;font:16px system-ui}h1{margin-top:0}.legend{line-height:1.7;color:#bdc6d5}.red{color:#ff4848}.blue{color:#70a0ff}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(340px,1fr));gap:18px}figure{margin:0;background:#1d2330;border-radius:8px;overflow:hidden}img{width:100%;display:block;cursor:zoom-in}figcaption{padding:12px}details{margin-top:8px;color:#ffcc77}p{max-width:1000px}</style>");
        html.Append($"<h1>Sprocket sample demo: {detected} / {results.Count} pairs located</h1><p class=\"legend\"><b class=\"red\">RED dot = bare backlight inside a sprocket hole</b><br><b class=\"blue\">BLUE dot = clear film between adjacent holes</b></p><p>Uses the shared FilmLight geometry detector on embedded RAF JPEGs / display images. These are sample locations only, NOT qualified RAW color measurements. Blank-light captures and unreliable pairs are flagged, not assigned invented film-base points. Click any image for a larger view. Originals are unchanged.</p><div class=\"grid\">");
        foreach (var result in results)
        {
            html.Append("<figure>");
            if (result.Output.Length > 0)
            {
                using var image = Image.FromFile(Path.Combine(output, result.Output));
                using var thumbnail = new Bitmap(image, new Size(800, Math.Max(1, (int)(800.0 * image.Height / image.Width))));
                using var stream = new MemoryStream();
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
                thumbnail.Save(stream, ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid), parameters);
                string data = "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray());
                html.Append($"<img src=\"{data}\" alt=\"{WebUtility.HtmlEncode(result.Input)}\" onclick=\"this.requestFullscreen()\">");
            }
            html.Append($"<figcaption><b>{WebUtility.HtmlEncode(result.Input)}</b> &mdash; {result.Status}");
            if (result.Output.Length > 0)
                html.Append($" &middot; <a style=\"color:#9fc4ff\" href=\"{Uri.EscapeDataString(result.Output)}\" target=\"_blank\">Full-size PNG</a>");
            if (result.Error is not null) html.Append($"<details><summary>Why no dots?</summary>{WebUtility.HtmlEncode(result.Error)}</details>");
            html.Append("</figcaption></figure>");
        }
        html.Append("</div></html>");
        File.WriteAllText(Path.Combine(output, "index.html"), html.ToString());
    }
}
