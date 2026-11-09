using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BitMiracle.LibTiff.Classic;
using FilmLight.Core;

[assembly: InternalsVisibleTo("FilmLight.Tests")]

namespace FilmLight.Workflow;

/// <summary>
/// Creates a processing-only snapshot; never save it over the capture documents.
/// Keep stagingDirectory stable across previews to reuse its content-addressed decode cache.
/// RAWs use fixed unity user WB, no exposure compensation, and the camera's standard matrix.
/// Physical exposure, focus, registration and illuminant stability must remain locked on camera.
/// Signed scene-linear samples are preserved; negadoctor handles its own density-domain floor.
/// </summary>
internal sealed class CameraInputDecoder(DarktableProcess cli)
{
    private const string Settings = "FilmLight-camera-input-v2-signed;darktable-5.6.2;standard-matrix;unity-user-WB;none;bilinear;CPU;float32;LIN_REC2020";
    private const string CameraInstructions =
        "Set IMAGE QUALITY to RAW or FINE+RAW and RAW RECORDING to UNCOMPRESSED or LOSSLESS COMPRESSED " +
        "(not COMPRESSED). Lock shutter speed, aperture, ISO, focus and illumination throughout the roll, then recapture.";
    private static readonly XNamespace Dt = "http://darktable.sf.net/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    internal async Task<RollDocument> DecodeAsync(RollDocument roll, RollCapture frame,
        string stagingDirectory, CancellationToken token, DevelopRecipe? recipe = null,
        IProgress<ProcessingProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        var selected = roll.Captures.SingleOrDefault(c => c.Id == frame.Id)
            ?? throw new InvalidOperationException("The selected frame must belong to this roll.");
        if (selected.Purpose != CapturePurpose.Frame || selected.IsDemo != roll.IsDemo)
            throw new InvalidOperationException("Select a frame with the same real/demo provenance as the roll.");
        var captures = new List<RollCapture> { selected };
        if (recipe?.PictureCalibration is { } picture)
        {
            picture.Validate(roll, selected.Mode);
            foreach (var sample in new[] { picture.Light, picture.FilmBase }.OfType<PictureSample>())
                if (captures.All(c => c.Id != sample.FrameId))
                    captures.Add(roll.Captures.Single(c => c.Id == sample.FrameId));
        }
        else
        {
            captures.Add(Reference(CapturePurpose.LightReference));
            if (roll.FilmKind != FilmKind.Positive) captures.Add(Reference(CapturePurpose.FilmBaseReference));
        }
        var inputs = captures.SelectMany(c => SelectFiles(c).Select(f => (Capture: c, File: f))).ToArray();
        string cache = Path.GetFullPath(Path.Combine(stagingDirectory, "camera-input-cache"));
        string? version = null;
        var replacements = new Dictionary<Guid, List<CaptureFile>>();
        Exposure? exposure = null;
        (int Width, int Height)? dimensions = null;
        for (int inputIndex = 0; inputIndex < inputs.Length; inputIndex++)
        {
            token.ThrowIfCancellationRequested();
            var (capture, file) = inputs[inputIndex];
            void Report(string stage, string message) => progress?.Report(new(frame.Id, stage,
                $"Input {inputIndex + 1} of {inputs.Length} - {CaptureLabel(capture)}, {file.Channel}: {message}"));
            string path = file.Path;
            int width, height;
            if (IsTiff(path))
            {
                Report("Reading linear input", "Checking image dimensions, color profile and linear samples.");
                var image = ReadQualified(path, capture.IsDemo, token);
                (width, height) = (image.Width, image.Height);
            }
            else
            {
                if (capture.IsDemo)
                    throw new InvalidDataException("DEMO captures must contain qualified synthetic TIFFs, not camera RAWs.");
                if (version is null)
                {
                    Report("Checking RAW decoder", "Checking the installed darktable version.");
                    string job = NewJob(cache);
                    version = await cli.RunAsync(["--version"], job, "version", token).ConfigureAwait(false);
                    if (!Regex.IsMatch(version, @"(?m)^darktable 5\.6\.2(?:\s|$)"))
                        throw new NotSupportedException("Camera input decoding requires darktable 5.6.2; no default-history fallback is permitted.");
                }
                var decoded = await DecodeRawAsync(path, cache, version, token, Report).ConfigureAwait(false);
                if (exposure is not null && !exposure.Matches(decoded.Exposure))
                    throw new InvalidDataException("Camera model or physical exposure differs from the sampled picture / references. Keep camera exposure fixed across the roll. " + CameraInstructions);
                exposure ??= decoded.Exposure;
                (path, width, height) = (decoded.Path, decoded.Width, decoded.Height);
            }
            if (dimensions is not null && dimensions.Value != (width, height))
                throw new InvalidDataException("Frame and sampled pictures / references must have identical decoded dimensions.");
            dimensions ??= (width, height);
            if (!replacements.TryGetValue(capture.Id, out var files))
                replacements[capture.Id] = files = [];
            files.Add(file with { Path = path });
        }
        token.ThrowIfCancellationRequested();
        return roll with
        {
            Captures = roll.Captures.Select(c => c with
            {
                Files = replacements.TryGetValue(c.Id, out var files) ? files.ToArray() : c.Files.ToArray()
            }).ToList(),
            Recipes = roll.Recipes.ToList(),
            Exports = roll.Exports.Select(e => e with { OutputFiles = e.OutputFiles.ToArray() }).ToList()
        };

        RollCapture Reference(CapturePurpose purpose) =>
            roll.Captures.Where(c => c.Purpose == purpose && c.Mode == selected.Mode &&
                    c.IsDemo == selected.IsDemo && c.CapturedAt <= selected.CapturedAt)
                .OrderBy(c => c.CapturedAt).ThenBy(c => c.Id).LastOrDefault()
            ?? throw new InvalidOperationException($"Missing matching preceding {selected.Mode} {purpose}. " + CameraInstructions);
    }

    private static IEnumerable<CaptureFile> SelectFiles(RollCapture capture)
    {
        LightChannel[] channels = capture.Mode switch
        {
            CaptureMode.White => [LightChannel.White],
            CaptureMode.Rgb => [LightChannel.Red, LightChannel.Green, LightChannel.Blue],
            _ => throw new InvalidDataException("Unknown capture mode.")
        };
        if (capture.Files.Any(f => !channels.Contains(f.Channel)))
            throw new InvalidDataException($"Unexpected light channel in capture {capture.Id}.");
        foreach (var channel in channels)
        {
            var files = capture.Files.Where(f => f.Channel == channel).ToArray();
            if (files.Length == 1 && (IsRaw(files[0].Path) || IsTiff(files[0].Path)))
                yield return files[0];
            else if (files.Length == 2 && files.Count(f => IsRaw(f.Path)) == 1 &&
                     files.Count(f => IsJpeg(f.Path)) == 1)
                yield return files.Single(f => IsRaw(f.Path));
            else
                throw new InvalidDataException($"{CaptureLabel(capture)}, {channel}: require one qualified TIFF, one RAF, or a RAF+JPEG pair. " +
                    "Camera JPEG is rendered, not linear RAW, and cannot be used for processing. " + CameraInstructions);
        }
    }

    private static string CaptureLabel(RollCapture capture) => capture.Purpose switch
    {
        CapturePurpose.Frame => $"Frame {capture.Number}",
        CapturePurpose.LightReference => "NO-FILM light reference",
        CapturePurpose.FilmBaseReference => "FILM BASE reference",
        _ => $"Capture {capture.Id}"
    };

    private async Task<Decoded> DecodeRawAsync(string original, string cache, string version, CancellationToken token,
        Action<string, string> report)
    {
        report("Checking RAW cache", "Reading the original RAW fingerprint for reusable decoded input.");
        await using var source = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Settings + "\n" + version + "\n" + hash)));
        string published = Path.Combine(cache, key);
        if (Directory.Exists(published))
        {
            report("Loading cached RAW", "Checking and reading the previously decoded linear image.");
            return await ReadCacheAsync(published, token).ConfigureAwait(false);
        }
        string job = NewJob(cache);
        try
        {
            source.Position = 0;
            report("Preparing RAW", "Copying the RAW into a private processing job.");
            string input = Path.Combine(job, "input.RAF");
            await using (var copy = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous))
                await source.CopyToAsync(copy, token).ConfigureAwait(false);
            WriteIdentityXmp(Path.Combine(job, "identity.xmp"), raw: true);
            report("Decoding RAW", "darktable is decoding the full-resolution sensor image. The first conversion can take a while.");
            string log = await ConvertAsync(cli, job, "input.RAF", "decoded.tif", token).ConfigureAwait(false);
            report("Validating decoded RAW", "Checking exposure metadata, color profile and linear samples.");
            string errors = await File.ReadAllTextAsync(Path.Combine(job, "decode.stderr.log"), token).ConfigureAwait(false);
            ValidateHistory(log, errors, raw: true);
            string output = Path.Combine(job, "decoded.tif");
            var physical = ReadExposure(output);
            ValidateFloatProfile(output);
            var image = ProcessingImages.ReadLinear(output, token);
            string result = Path.Combine(job, "linear.tif");
            report("Saving RAW cache", "Saving and verifying reusable linear input for later previews.");
            ProcessingImages.WriteTiff(result, image, true, ProcessingImages.LinearLabel, token,
                ProcessingImages.LinearRec2020Profile());
            _ = ReadQualified(result, false, token);
            var receipt = new Receipt(image.Width, image.Height, physical,
                Convert.ToHexString(await HashFileAsync(result, token).ConfigureAwait(false)));
            await File.WriteAllTextAsync(Path.Combine(job, "receipt.json"), JsonSerializer.Serialize(receipt), token).ConfigureAwait(false);
            File.Delete(input);
            File.Delete(output);
            token.ThrowIfCancellationRequested();
            try { Directory.Move(job, published); }
            catch (IOException) when (Directory.Exists(published))
            {
                // Another decoder published this same immutable content while our CLI was running.
                return await ReadCacheAsync(published, token).ConfigureAwait(false);
            }
            return new(Path.Combine(published, "linear.tif"), receipt.Width, receipt.Height, physical);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or OperationCanceledException or NotSupportedException or TimeoutException
            or System.ComponentModel.Win32Exception or JsonException)
        {
            await File.WriteAllTextAsync(Path.Combine(job, "failure.txt"), error.ToString(), CancellationToken.None).ConfigureAwait(false);
            if (error is OperationCanceledException) throw;
            throw new InvalidDataException($"RAF decode failed; no qualified input was published. {CameraInstructions} " +
                $"Inspect logs at {job}. {error.Message}", error);
        }
    }

    private static async Task<Decoded> ReadCacheAsync(string directory, CancellationToken token)
    {
        var receipt = JsonSerializer.Deserialize<Receipt>(
            await File.ReadAllTextAsync(Path.Combine(directory, "receipt.json"), token).ConfigureAwait(false))
            ?? throw new InvalidDataException($"Invalid decode cache receipt: {directory}");
        string path = Path.Combine(directory, "linear.tif");
        if (!string.Equals(receipt.Sha256, Convert.ToHexString(await HashFileAsync(path, token).ConfigureAwait(false)), StringComparison.Ordinal))
            throw new InvalidDataException($"Corrupt camera decode cache; remove this cache entry and retry: {directory}");
        ValidateFloatProfile(path);
        var image = ReadQualified(path, false, token);
        if (image.Width != receipt.Width || image.Height != receipt.Height || receipt.Exposure is null || !receipt.Exposure.IsValid)
            throw new InvalidDataException($"Invalid camera decode cache dimensions/exposure: {directory}");
        return new(path, image.Width, image.Height, receipt.Exposure);
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        return await SHA256.HashDataAsync(file, token).ConfigureAwait(false);
    }

    private static string NewJob(string cache)
    {
        string path = Path.Combine(cache, "decode-" + Guid.NewGuid().ToString("N") + ".partial");
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool IsRaw(string path) => Path.GetExtension(path).Equals(".raf", StringComparison.OrdinalIgnoreCase);
    private static bool IsTiff(string path) => Path.GetExtension(path).ToLowerInvariant() is ".tif" or ".tiff";
    private static bool IsJpeg(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg";

    private static LinearImage ReadQualified(string path, bool demo, CancellationToken token)
    {
        using (var tif = Tiff.Open(path, "r") ?? throw new InvalidDataException($"Cannot decode TIFF: {path}"))
            if ((tif.GetField(TiffTag.IMAGEDESCRIPTION)?[0].ToString() ?? "")
                .Contains(ProcessingImages.SyntheticLabel, StringComparison.Ordinal) != demo)
                throw new InvalidDataException("TIFF synthetic label and roll provenance disagree; synthetic data cannot be relabeled as real.");
        return ProcessingImages.ReadLinear(path, token, requireQualification: true);
    }

    internal static void WriteIdentityXmp(string path, bool raw)
    {
        var history = new XElement(Rdf + "Seq");
        // Pinned 5.6.2 layouts: temperature v4 is four floats + user preset; colorin v7 is 1044 bytes.
        var temperature = new byte[20];
        for (int c = 0; c < 4; c++) BinaryPrimitives.WriteSingleLittleEndian(temperature.AsSpan(c * 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(temperature.AsSpan(16), 2);
        var color = new byte[1044];
        BinaryPrimitives.WriteInt32LittleEndian(color, raw ? 11 : 9); // standard camera matrix / embedded ICC
        BinaryPrimitives.WriteInt32LittleEndian(color.AsSpan(516), 1); // relative colorimetric
        BinaryPrimitives.WriteInt32LittleEndian(color.AsSpan(528), 4); // linear Rec2020 working space
        Add("temperature", 4, raw, temperature);
        Add("colorin", 7, true, color);
        if (raw)
        {
            // Empty auto-init parameters retain per-file CFA geometry and black/white levels.
            Add("rawprepare", 2, true, []);
            Add("demosaic", 4, true, []);
        }
        foreach (string operation in new[] { "exposure", "basecurve", "filmicrgb", "sigmoid", "tonecurve",
            "rgbcurve", "levels", "rgblevels", "shadhi", "highlights", "sharpen", "colorbalancergb",
            "channelmixerrgb", "negadoctor", "lens" })
            Add(operation, 1, false, []);
        var description = new XElement(Rdf + "Description",
            new XAttribute(XNamespace.Xmlns + "darktable", Dt), new XAttribute(Dt + "xmp_version", 5),
            new XAttribute(Dt + "raw_params", 0), new XAttribute(Dt + "auto_presets_applied", 1),
            new XAttribute(Dt + "iop_order_version", 2), new XAttribute(Dt + "history_end", history.Elements().Count()),
            new XElement(Dt + "history", history));
        new XDocument(new XElement(XName.Get("xmpmeta", "adobe:ns:meta/"),
            new XAttribute(XNamespace.Xmlns + "x", "adobe:ns:meta/"),
            new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf), description))).Save(path);

        void Add(string operation, int version, bool enabled, byte[] parameters) =>
            history.Add(new XElement(Rdf + "li", new XAttribute(Dt + "num", history.Elements().Count()),
                new XAttribute(Dt + "operation", operation), new XAttribute(Dt + "enabled", enabled ? 1 : 0),
                new XAttribute(Dt + "modversion", version), new XAttribute(Dt + "params", Convert.ToHexString(parameters).ToLowerInvariant()),
                new XAttribute(Dt + "multi_priority", 0), new XAttribute(Dt + "multi_name", ""),
                new XAttribute(Dt + "blendop_version", 13)));
    }

    internal static Task<string> ConvertAsync(DarktableProcess process, string directory,
        string input, string output, CancellationToken token) =>
        process.RunAsync([input, "identity.xmp", output, "--icc-type", "LIN_REC2020",
            "--icc-intent", "RELATIVE_COLORIMETRIC", "--apply-custom-presets", "0", "--verbose",
            "--core", "--configdir", Path.Combine(directory, "config"),
            "--cachedir", Path.Combine(directory, "cache"), "--disable-opencl",
            "--conf", "plugins/darkroom/workflow=none",
            "--conf", "ui_last/gui_language=C",
            "--conf", "plugins/imageio/format/tiff/bpp=32",
            "--conf", "plugins/imageio/format/tiff/compress=0",
            "--conf", "plugins/imageio/format/tiff/shortfile=0",
            "--conf", "plugins/lighttable/export/pixel_interpolator=bilinear",
            "--conf", "plugins/lighttable/export/pixel_interpolator_warp=bilinear",
            "--conf", "write_sidecar_files=never"], directory, "decode", token);

    internal static void ValidateHistory(string log, string errors, bool raw)
    {
        string[] allowed = ["raw black/white point", "white balance", "demosaic", "input color profile",
            "output color profile", "orientation", "display encoding"];
        var enabled = Regex.Matches(log, @"(?m)^\s*\u25cf\s+([^\r\n]+)").Select(m => m.Groups[1].Value.Trim()).ToHashSet();
        if (!enabled.Contains("input color profile") ||
            (raw && new[] { "raw black/white point", "white balance", "demosaic" }.Any(m => !enabled.Contains(m))) ||
            enabled.Any(m => !allowed.Contains(m)) ||
            Regex.IsMatch(log + "\n" + errors,
                @"(?i)color matrix not found|input profile could not|unsupported|failed to|version mismatch|unknown version|not supported"))
            throw new InvalidDataException("darktable did not apply the qualified identity/camera-profile pipeline; inspect decode logs/XMP. No default-profile or tone-mapping fallback is allowed.");
    }

    internal static void ValidateFloatProfile(string path)
    {
        using var tif = Tiff.Open(path, "r") ?? throw new InvalidDataException($"Missing decoded TIFF: {path}");
        if (tif.GetField(TiffTag.BITSPERSAMPLE)?[0].ToInt() != 32 ||
            tif.GetFieldDefaulted(TiffTag.SAMPLEFORMAT)[0].ToInt() != (int)SampleFormat.IEEEFP)
            throw new InvalidDataException("Camera decode did not produce 32-bit floating-point TIFF.");
        byte[] profile = tif.GetField(TiffTag.ICCPROFILE)?[1].ToByteArray() ?? [];
        byte[] expected = ProcessingImages.LinearRec2020Profile();
        if (profile.Length < 132 || !profile.AsSpan(36, 4).SequenceEqual("acsp"u8) ||
            !profile.AsSpan(16, 8).SequenceEqual("RGB XYZ "u8))
            throw new InvalidDataException("Decoded TIFF has no RGB matrix ICC profile.");
        var tags = ProfileTags(profile);
        var reference = ProfileTags(expected);
        foreach (string channel in new[] { "r", "g", "b" })
        {
            if (!tags.TryGetValue(channel + "XYZ", out var xyz) || xyz.Length < 20 ||
                !xyz.AsSpan(0, 4).SequenceEqual("XYZ "u8))
                throw new InvalidDataException("Decoded ICC lacks Rec2020 primaries.");
            for (int c = 0; c < 3; c++)
                if (Math.Abs(BinaryPrimitives.ReadInt32BigEndian(xyz.AsSpan(8 + c * 4)) / 65536.0 -
                    BinaryPrimitives.ReadInt32BigEndian(reference[channel + "XYZ"].AsSpan(8 + c * 4)) / 65536.0) >= .003)
                    throw new InvalidDataException("Decoded ICC primaries are not Rec2020.");
            if (!tags.TryGetValue(channel + "TRC", out var trc) || trc.Length < 12 ||
                !((trc.AsSpan(0, 4).SequenceEqual("curv"u8) &&
                   (BinaryPrimitives.ReadUInt32BigEndian(trc.AsSpan(8)) == 0 ||
                    (trc.Length >= 14 && BinaryPrimitives.ReadUInt32BigEndian(trc.AsSpan(8)) == 1 &&
                     BinaryPrimitives.ReadUInt16BigEndian(trc.AsSpan(12)) == 256))) ||
                  (trc.Length >= 16 && trc.AsSpan(0, 4).SequenceEqual("para"u8) &&
                   BinaryPrimitives.ReadUInt16BigEndian(trc.AsSpan(8)) == 0 &&
                   BinaryPrimitives.ReadInt32BigEndian(trc.AsSpan(12)) == 65536)))
                throw new InvalidDataException("Decoded ICC transfer curve is not linear.");
        }
    }

    private static Dictionary<string, byte[]> ProfileTags(byte[] data)
    {
        uint count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(128));
        if (count > (data.Length - 132) / 12) throw new InvalidDataException("Malformed decoded ICC tag table.");
        var tags = new Dictionary<string, byte[]>();
        for (int i = 0; i < count; i++)
        {
            var entry = data.AsSpan(132 + i * 12, 12);
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            if (offset > data.Length || size > data.Length - offset)
                throw new InvalidDataException("Malformed decoded ICC tag extent.");
            tags[Encoding.ASCII.GetString(entry[..4])] = data.AsSpan((int)offset, (int)size).ToArray();
        }
        return tags;
    }

    private static Exposure ReadExposure(string path)
    {
        using var tif = Tiff.Open(path, "r") ?? throw new InvalidDataException("Cannot read decoded exposure metadata.");
        string make = tif.GetField(TiffTag.MAKE)?[0].ToString() ?? "";
        string model = tif.GetField(TiffTag.MODEL)?[0].ToString() ?? "";
        long offset = tif.GetField(TiffTag.EXIFIFD)?[0].ToLong() ?? 0;
        if (offset <= 0 || !tif.ReadEXIFDirectory(offset))
            throw new InvalidDataException("RAW decode lacks EXIF exposure metadata; matching physical exposures cannot be verified.");
        var iso = tif.GetField(TiffTag.EXIF_ISOSPEEDRATINGS);
        short[] sensitivity = iso?[1].ToShortArray() ?? [];
        var exposure = new Exposure(make, model, tif.GetField(TiffTag.EXIF_EXPOSURETIME)?[0].ToDouble() ?? 0,
            tif.GetField(TiffTag.EXIF_FNUMBER)?[0].ToDouble() ?? 0, sensitivity.Length == 1 ? (ushort)sensitivity[0] : 0);
        if (!exposure.IsValid || !make.Contains("FUJI", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected Fujifilm RAW with camera model, shutter speed, aperture and ISO metadata.");
        return exposure;
    }

    private sealed record Exposure(string Make, string Model, double Seconds, double Aperture, int Iso)
    {
        public bool IsValid => !string.IsNullOrWhiteSpace(Make) && !string.IsNullOrWhiteSpace(Model) &&
            double.IsFinite(Seconds) && Seconds > 0 && double.IsFinite(Aperture) && Aperture > 0 && Iso > 0;
        public bool Matches(Exposure other) => Make == other.Make && Model == other.Model && Iso == other.Iso &&
            Math.Abs(Seconds - other.Seconds) <= Seconds * .00001 &&
            Math.Abs(Aperture - other.Aperture) <= Aperture * .00001;
    }
    private sealed record Receipt(int Width, int Height, Exposure Exposure, string Sha256);
    private sealed record Decoded(string Path, int Width, int Height, Exposure Exposure);
}
