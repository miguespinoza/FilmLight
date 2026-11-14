using FilmLight.Processing;
using System.Text.Json;
using BitMiracle.LibTiff.Classic;

namespace FilmLight.Tests.Processing;

public static class AdapterTests
{
    public static (string Name, Func<ValueTask> Body)[] All =>
        [("Negative renderer golden packing and capabilities", () => new ValueTask(RunAsync()))];

    public static async Task RunAsync()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "lab-matched.xmp");
        var (light, parameters) = await DarktableCliNegativeRenderer.ReadProfileAsync(fixture, default);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(fixture, ".json")));
        Near(parameters.FilmBase.R, json.RootElement.GetProperty("Dmin")[0].GetDouble());
        Near(parameters.FilmBase.G, json.RootElement.GetProperty("Dmin")[1].GetDouble());
        Near(parameters.FilmBase.B, json.RootElement.GetProperty("Dmin")[2].GetDouble());
        Near(parameters.DensityRange, json.RootElement.GetProperty("Dmax").GetDouble());
        Near(parameters.ScanExposureBias, json.RootElement.GetProperty("scan_exposure_bias").GetDouble());
        Near(parameters.PaperBlack, json.RootElement.GetProperty("paper_black").GetDouble());
        Near(parameters.PaperGrade, json.RootElement.GetProperty("paper_grade").GetDouble());
        Near(parameters.PaperGloss, json.RootElement.GetProperty("paper_gloss").GetDouble());
        Near(parameters.PrintExposure, json.RootElement.GetProperty("print_exposure_multiplier").GetDouble());
        var highlights = json.RootElement.GetProperty("highlights_white_balance");
        var shadows = json.RootElement.GetProperty("shadows_color_cast");
        Near(parameters.HighlightsBalance.R, highlights[0].GetDouble());
        Near(parameters.HighlightsBalance.G, highlights[1].GetDouble());
        Near(parameters.HighlightsBalance.B, highlights[2].GetDouble());
        Near(parameters.ShadowsBalance.R, shadows[0].GetDouble());
        Near(parameters.ShadowsBalance.G, shadows[1].GetDouble());
        Near(parameters.ShadowsBalance.B, shadows[2].GetDouble());
        var packed = DarktableHistory.Pack(parameters);
        Assert(packed == "010000007df9ca3cbbb3443c0543813b0000803f30f4d83f5b60903f6356863f0000803ff9c01f3f0000004092a1a23e0000803f0e53733f03099fbd0000003f30b3c4407bda343f6c88353f", "Golden negadoctor bytes changed.");
        var temperature = DarktableHistory.UnpackTemperature(DarktableHistory.PackTemperature(light.Coefficients!.Value));
        Near(temperature.R, light.Coefficients.Value.R);
        Near(temperature.G, 1);
        Near(temperature.B, light.Coefficients.Value.B);
        var start = DarktableCliNegativeRenderer.BuildStartInfo("cli with spaces", "workspace with spaces", "isolated config",
            "source.RAF", "result.jpg", false, false, 1600);
        Assert(start.ArgumentList.Take(3).SequenceEqual(new[] { "source.RAF", "history.xmp", "result.jpg" }), "Render paths must remain relative arguments.");
        Assert(start.WorkingDirectory == "workspace with spaces" && !start.UseShellExecute, "Render must not use a shell.");
        Assert(start.ArgumentList.Contains(":memory:") && start.ArgumentList.Contains("write_sidecar_files=never") &&
            start.ArgumentList.Contains("--disable-opencl") && start.ArgumentList.Contains("plugins/darkroom/workflow=none"), "Private rendering settings changed.");
        var full = DarktableCliNegativeRenderer.BuildStartInfo("cli", "work", "export-config", "source.RAF", "result.tif", false, true, 1600);
        Assert(!full.ArgumentList.Contains("--width") && !full.ArgumentList.Contains("--height"), "Exports must retain full resolution.");
        var unsupported = new UnsupportedNegativeRenderer("test unavailable");
        Assert(!(await unsupported.GetCapabilitiesAsync(default)).IsSupported, "Unsupported renderer must report unavailable.");
        try
        {
            await unsupported.RenderLinearAsync(new("missing", light, parameters), "unused", default);
            throw new Exception("Unsupported rendering succeeded.");
        }
        catch (NotSupportedException) { }
        string validationRoot = Path.Combine(AppContext.BaseDirectory, "structure-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(validationRoot);
        try
        {
            byte[] jpeg = [0xff, 0xd8, 0xff, 0xc0, 0, 11, 8, 0, 1, 0, 1, 1, 1, 0x11, 0,
                0xff, 0xda, 0, 8, 1, 1, 0, 0, 63, 0, 0x12, 0xff, 0xd9];
            string jpegPath = Path.Combine(validationRoot, "scan.jpg");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "positive-preview.jpg"), jpegPath);
            Assert(DarktableCliNegativeRenderer.ValidateOutput(jpegPath, false, false) == (1600, 1067), "A real JPEG could not be decoded.");
            byte[] unsupportedCoding = await File.ReadAllBytesAsync(jpegPath);
            for (int offset = 2; offset < unsupportedCoding.Length;)
            {
                int marker = unsupportedCoding[offset + 1], length = (unsupportedCoding[offset + 2] << 8) | unsupportedCoding[offset + 3];
                if (marker is 0xc0 or 0xc2)
                {
                    unsupportedCoding[offset + 4] = 12;
                    break;
                }
                offset += length + 2;
            }
            await File.WriteAllBytesAsync(jpegPath, unsupportedCoding);
            RejectTruncated(jpegPath, false);
            // Dimensions, SOS and EOI are present, but there are no usable coding tables.
            await File.WriteAllBytesAsync(jpegPath, jpeg);
            RejectTruncated(jpegPath, false);
            await File.WriteAllBytesAsync(jpegPath, jpeg[..^1]);
            RejectTruncated(jpegPath, false);
            await File.WriteAllBytesAsync(jpegPath, jpeg[..15].Concat(new byte[] { 0xff, 0xd9 }).ToArray());
            RejectTruncated(jpegPath, false);
            string tiffPath = Path.Combine(validationRoot, "strip.tif");
            using (var image = Tiff.Open(tiffPath, "w"))
            {
                image.SetField(TiffTag.IMAGEWIDTH, 8);
                image.SetField(TiffTag.IMAGELENGTH, 8);
                image.SetField(TiffTag.SAMPLESPERPIXEL, 1);
                image.SetField(TiffTag.BITSPERSAMPLE, 16);
                image.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
                image.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
                image.SetField(TiffTag.COMPRESSION, Compression.DEFLATE);
                image.SetField(TiffTag.ROWSPERSTRIP, 2);
                for (int row = 0; row < 8; row++) Assert(image.WriteScanline(new byte[16], row), "TIFF fixture encoding failed.");
            }
            Assert(DarktableCliNegativeRenderer.ValidateOutput(tiffPath, false, true) == (8, 8), "A compressed 16-bit TIFF could not be decoded.");
            byte[] tiffBytes = await File.ReadAllBytesAsync(tiffPath);
            long stripOffset;
            using (var image = Tiff.Open(tiffPath, "r")) stripOffset = image.GetField(TiffTag.STRIPOFFSETS)[0].TolongArray()[0];
            var corrupt = (byte[])tiffBytes.Clone();
            Array.Clear(corrupt, checked((int)stripOffset), 8);
            await File.WriteAllBytesAsync(tiffPath, corrupt);
            RejectTruncated(tiffPath, true);
            await File.WriteAllBytesAsync(tiffPath, tiffBytes);
            using (var stream = File.OpenWrite(tiffPath)) stream.SetLength(stream.Length - 1);
            RejectTruncated(tiffPath, true);
        }
        finally { Directory.Delete(validationRoot, recursive: true); }
        if (OperatingSystem.IsWindows())
        {
            var process = new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" }) process.ArgumentList.Add(argument);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await DarktableCliNegativeRenderer.RunProcessAsync(process, TimeSpan.FromMinutes(1), cancel.Token);
                throw new Exception("Cancelled renderer process succeeded.");
            }
            catch (OperationCanceledException) { }
            Assert(watch.Elapsed < TimeSpan.FromSeconds(10), "Cancellation did not terminate the process tree promptly.");
        }
    }

    internal static void Near(double actual, double expected)
    {
        if (Math.Abs(actual - expected) > 0.000001) throw new Exception($"Expected {expected}, found {actual}.");
    }

    internal static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void RejectTruncated(string path, bool tiff)
    {
        try
        {
            DarktableCliNegativeRenderer.ValidateOutput(path, false, tiff);
            throw new Exception("Truncated or undecodable image data was accepted.");
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException) { }
    }
}
