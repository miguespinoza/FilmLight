using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BitMiracle.LibTiff.Classic;
using FilmLight.Core;

namespace FilmLight.Workflow;

internal sealed record Exposure(string Make, string Model, double Seconds, double Aperture, int Iso)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Make) && !string.IsNullOrWhiteSpace(Model) &&
        double.IsFinite(Seconds) && Seconds > 0 && double.IsFinite(Aperture) && Aperture > 0 && Iso > 0;
    public bool Matches(Exposure other) => Make == other.Make && Model == other.Model && Iso == other.Iso &&
        Math.Abs(Seconds - other.Seconds) <= Seconds * .00001 &&
        Math.Abs(Aperture - other.Aperture) <= Aperture * .00001;
}

internal sealed record PatchStats(double[] Mean, float[] Minimum, float[] Maximum, int PixelCount,
    bool IsClipped, bool IsDarkOrInvalid, int SourceWidth, int SourceHeight);

internal sealed class FrameSource(FrameSources owner, RollCapture capture)
{
    internal Task<LinearImage> GetImageAsync(int minLongEdge, CancellationToken token,
        IProgress<ProcessingProgress>? progress = null) => owner.GetImageAsync(capture, minLongEdge, token, progress);

    internal Task<PatchStats> MeasureAsync(double x, double y, int radius, CancellationToken token,
        IProgress<ProcessingProgress>? progress = null) => owner.MeasureAsync(capture, x, y, radius, token, progress);

    internal Task<Exposure?> GetExposureAsync(CancellationToken token) => owner.GetExposureAsync(capture, token);
}

// Owns immutable cache entries; every image handed to a consumer is a defensive copy.
internal sealed class FrameSources : IDisposable
{
    internal const int FullResolution = int.MaxValue;
    private const int ProxyEdge = 2560;
    private const string Settings = "FilmLight-camera-input-v4-full-pfm-proxy-tiff;darktable-5.6.2;standard-matrix;unity-user-WB;none;bilinear;CPU;float32;LIN_REC2020";
    private const string CameraInstructions =
        "Set IMAGE QUALITY to RAW or FINE+RAW and RAW RECORDING to UNCOMPRESSED or LOSSLESS COMPRESSED " +
        "(not COMPRESSED). Each frame is calibrated independently; choose focus mode on the camera.";
    internal const string ExposureMismatch =
        "Camera model or physical exposure differs within this RGB set or from legacy separate references. " +
        "Use this frame's own calibration; RGB channels must use matching exposures. " + CameraInstructions;
    private readonly string cache;
    private readonly DarktableProcess? cli;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Fingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Task<Entry> Task, ProgressSinks Progress)> pending = [];
    private readonly Dictionary<string, Entry> images = [];
    private readonly Dictionary<string, (Exposure? Value, Decoded[] Decodes)> exposures = [];
    private readonly LinkedList<string> proxies = [];
    private string? full;
    private readonly Queue<Work> proxyQueue = new(), fullQueue = new();
    private bool draining, disposed;
    private Task<string>? version;
    private Task? drain;
    private sealed record Work(Func<Task> Run);
    private sealed record Entry(LinearImage Image, Exposure? Exposure, Decoded[] Decodes);
    private sealed record Receipt(int Width, int Height, Exposure Exposure, string? Sha256, long Length, long LastWriteUtcTicks);
    private sealed record Decoded(string Path, Receipt Receipt);
    private sealed class ProgressSinks : IProgress<ProcessingProgress>
    {
        private readonly object gate = new();
        private readonly List<IProgress<ProcessingProgress>> sinks = [];
        internal void Add(IProgress<ProcessingProgress> sink) { lock (gate) sinks.Add(sink); }
        internal void Remove(IProgress<ProcessingProgress> sink) { lock (gate) sinks.Remove(sink); }
        public void Report(ProcessingProgress value)
        {
            IProgress<ProcessingProgress>[] current;
            lock (gate) current = sinks.ToArray();
            foreach (var sink in current) sink.Report(value);
        }
    }

    internal FrameSources(string cacheDirectory, string? darktableCliPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        cache = Path.GetFullPath(cacheDirectory);
        cli = darktableCliPath is null ? null : new(darktableCliPath);
        if (!Directory.Exists(cache)) return;
        foreach (string directory in Directory.GetDirectories(cache, "decode-*.partial"))
            if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddHours(-1))
                Directory.Delete(directory, true);
        TrimFailures();
    }

    internal FrameSource Open(RollDocument roll, RollCapture capture)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var selected = roll.Captures.SingleOrDefault(c => c.Id == capture.Id)
            ?? throw new InvalidOperationException("The selected frame must belong to this roll.");
        if (selected.IsDemo != roll.IsDemo || capture.IsDemo != roll.IsDemo)
            throw new InvalidOperationException("Real and synthetic captures cannot be mixed.");
        return new(this, capture);
    }

    internal void Prefetch(RollDocument roll, RollCapture capture)
    {
        var source = Open(roll, capture);
        _ = WarmAsync();
        async Task WarmAsync()
        {
            try { await source.GetImageAsync(ProxyEdge, lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or NotSupportedException
                or UnauthorizedAccessException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                // Foreground requests retry and surface the error; warm-up has no UI owner.
                System.Diagnostics.Trace.TraceError($"Frame prefetch {capture.Id} failed: {error}");
            }
        }
    }

    internal Task<string> GetVersionAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (version is { IsCompletedSuccessfully: false, IsCompleted: true }) version = null;
            return (version ??= CheckVersionAsync()).WaitAsync(token);
        }
    }

    private async Task<string> CheckVersionAsync()
    {
        if (cli is null) throw new InvalidOperationException("Select darktable-cli.exe to decode camera RAWs.");
        string text = await cli.RunAsync(["--version"], Environment.CurrentDirectory, "version", lifetime.Token,
            writeLogs: false).ConfigureAwait(false);
        if (!Regex.IsMatch(text, @"(?m)^darktable 5\.6\.2(?:\s|$)"))
            throw new NotSupportedException("Camera input decoding requires darktable 5.6.2; no default-history fallback is permitted.");
        return text;
    }

    internal async Task<LinearImage> GetImageAsync(RollCapture capture, int minLongEdge, CancellationToken token,
        IProgress<ProcessingProgress>? progress)
    {
        if (minLongEdge <= 0) throw new ArgumentOutOfRangeException(nameof(minLongEdge));
        var entry = await GetEntryAsync(capture, minLongEdge <= ProxyEdge, token, progress).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return entry.Image with { Pixels = (float[])entry.Image.Pixels.Clone() };
    }

    internal async Task<Exposure?> GetExposureAsync(RollCapture capture, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var files = SelectFiles(capture).ToArray();
        string identity = CaptureIdentity(capture, files);
        (Exposure? Value, Decoded[] Decodes) known;
        bool found;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            found = exposures.TryGetValue(identity, out known);
        }
        if (found)
        {
            ValidateBackingFiles(known.Decodes);
            return known.Value;
        }
        if (!capture.IsDemo && files.All(f => IsRaw(f.Path)))
        {
            string text = await GetVersionAsync(token).ConfigureAwait(false);
            var receipts = new List<Decoded>();
            foreach (var file in files)
            {
                string directory = await RawCacheDirectoryAsync(file.Path, true, text, token).ConfigureAwait(false);
                if (!Directory.Exists(directory)) break;
                receipts.Add(await ReadCacheAsync(directory, true, token).ConfigureAwait(false));
            }
            if (receipts.Count == files.Length)
            {
                var exposure = receipts[0].Receipt.Exposure;
                if (receipts.Any(r => !exposure.Matches(r.Receipt.Exposure)))
                    throw new InvalidDataException(ExposureMismatch);
                lock (gate) exposures[identity] = (exposure, receipts.ToArray());
                return exposure;
            }
        }
        return (await GetEntryAsync(capture, true, token, null).ConfigureAwait(false)).Exposure;
    }

    internal async Task<PatchStats> MeasureAsync(RollCapture capture, double x, double y, int radius, CancellationToken token,
        IProgress<ProcessingProgress>? progress)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x >= 1 || y < 0 || y >= 1 || radius < 0)
            throw new ArgumentOutOfRangeException(nameof(x), "Pick a point inside the displayed image.");
        var image = (await GetEntryAsync(capture, false, token, progress).ConfigureAwait(false)).Image;
        int cx = (int)(x * image.Width), cy = (int)(y * image.Height);
        return Measure(image, (int)Math.Max(0, (long)cx - radius), (int)Math.Max(0, (long)cy - radius),
            (int)Math.Min(image.Width - 1L, (long)cx + radius), (int)Math.Min(image.Height - 1L, (long)cy + radius), token);
    }

    private static PatchStats Measure(LinearImage image, int left, int top, int right, int bottom, CancellationToken token)
    {
        double[] sum = [0, 0, 0];
        float[] min = [float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity];
        float[] max = [float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity];
        int count = 0;
        bool clipped = false, invalid = false;
        for (int y = top; y <= bottom; y++)
        for (int x = left; x <= right; x++)
        {
            token.ThrowIfCancellationRequested();
            for (int c = 0; c < 3; c++)
            {
                float v = image.Pixels[(y * image.Width + x) * 3 + c];
                sum[c] += v;
                min[c] = Math.Min(min[c], v);
                max[c] = Math.Max(max[c], v);
                clipped |= v >= .99999f;
                invalid |= !float.IsFinite(v) || v <= .0001f;
            }
            count++;
        }
        return new(sum.Select(v => v / count).ToArray(), min, max, count, clipped, invalid, image.Width, image.Height);
    }

    private Task<Entry> GetEntryAsync(RollCapture capture, bool proxy, CancellationToken token,
        IProgress<ProcessingProgress>? progress)
    {
        token.ThrowIfCancellationRequested();
        var files = SelectFiles(capture).ToArray();
        string identity = CaptureIdentity(capture, files);
        string key = identity + (proxy ? ":proxy" : ":full");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (images.TryGetValue(key, out var cached))
            {
                ValidateBackingFiles(cached.Decodes);
                Touch(key, proxy);
                return Task.FromResult(cached);
            }
            if (!pending.TryGetValue(key, out var load))
            {
                var completion = new TaskCompletionSource<Entry>(TaskCreationOptions.RunContinuationsAsynchronously);
                var sinks = new ProgressSinks();
                pending.Add(key, load = (completion.Task, sinks));
                (proxy ? proxyQueue : fullQueue).Enqueue(new(async () =>
                {
                    try
                    {
                        var entry = await ComposeAsync(capture, files, proxy, sinks).ConfigureAwait(false);
                        lock (gate)
                        {
                            images[key] = entry;
                            exposures[identity] = (entry.Exposure,
                                entry.Decodes.Where(d => Path.GetExtension(d.Path) == ".tif").ToArray());
                            Touch(key, proxy);
                        }
                        completion.SetResult(entry);
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                    { completion.SetCanceled(lifetime.Token); }
                    catch (Exception error) { completion.SetException(error); }
                    finally { lock (gate) pending.Remove(key); }
                }));
                if (!draining)
                {
                    draining = true;
                    drain = Task.Run(DrainAsync);
                }
            }
            if (progress is not null) load.Progress.Add(progress);
            return WaitAsync(load.Task, load.Progress, progress, token);
        }
    }

    private static async Task<Entry> WaitAsync(Task<Entry> task, ProgressSinks sinks,
        IProgress<ProcessingProgress>? progress, CancellationToken token)
    {
        try { return await task.WaitAsync(token).ConfigureAwait(false); }
        finally { if (progress is not null) sinks.Remove(progress); }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            Work work;
            lock (gate)
            {
                if (proxyQueue.Count > 0) work = proxyQueue.Dequeue();
                else if (fullQueue.Count > 0) work = fullQueue.Dequeue();
                else { draining = false; return; }
            }
            await work.Run().ConfigureAwait(false);
        }
    }

    private void Touch(string key, bool proxy)
    {
        if (!proxy)
        {
            if (full is not null && full != key) images.Remove(full);
            full = key;
            return;
        }
        proxies.Remove(key);
        proxies.AddFirst(key);
        while (proxies.Count > 8)
        {
            images.Remove(proxies.Last!.Value);
            proxies.RemoveLast();
        }
    }

    private async Task<Entry> ComposeAsync(RollCapture capture, CaptureFile[] files, bool proxy,
        IProgress<ProcessingProgress>? progress)
    {
        var token = lifetime.Token;
        token.ThrowIfCancellationRequested();
        LinearImage? result = null;
        Exposure? exposure = null;
        var decodes = new List<Decoded>();
        foreach (var file in files)
        {
            void Report(string stage, string message) => progress?.Report(new(capture.Id, stage,
                $"{CaptureLabel(capture)}, {file.Channel}: {message}"));
            LinearImage source;
            if (IsTiff(file.Path))
            {
                Report("Reading linear input", "Checking image dimensions, color profile and linear samples.");
                source = ReadQualified(file.Path, capture.IsDemo, token);
                if (proxy) source = AreaAverage(source, ProxyEdge, token);
            }
            else
            {
                if (capture.IsDemo)
                    throw new InvalidDataException("DEMO captures must contain qualified synthetic TIFFs, not camera RAWs.");
                Report("Checking RAW decoder", "Checking the installed darktable version.");
                string text = await GetVersionAsync(token).ConfigureAwait(false);
                var (decoded, image, metadata) = await DecodeRawAsync(file.Path, proxy, text, Report, token).ConfigureAwait(false);
                decodes.Add(decoded);
                if (metadata is not null) decodes.Add(metadata);
                if (exposure is not null && !exposure.Matches(decoded.Receipt.Exposure))
                    throw new InvalidDataException(ExposureMismatch);
                exposure ??= decoded.Receipt.Exposure;
                source = image;
            }
            if (capture.Mode == CaptureMode.White) return new(source, exposure, decodes.ToArray());
            result ??= new(source.Width, source.Height, new float[source.Pixels.Length]);
            if (result.Width != source.Width || result.Height != source.Height)
                throw new InvalidDataException("Frame channels and any reference images must have identical dimensions and registration.");
            int channel = file.Channel switch { LightChannel.Red => 0, LightChannel.Green => 1, _ => 2 };
            for (int i = channel; i < source.Pixels.Length; i += 3)
            {
                if ((i & 65535) < 3) token.ThrowIfCancellationRequested();
                result.Pixels[i] = source.Pixels[i];
            }
        }
        return new(result ?? throw new InvalidDataException("Capture has no channels."), exposure, decodes.ToArray());
    }

    private async Task<(Decoded Decode, LinearImage Image, Decoded? Metadata)> DecodeRawAsync(string original, bool proxy, string text,
        Action<string, string> report, CancellationToken token)
    {
        if (cli is null) throw new InvalidOperationException("Select darktable-cli.exe to decode camera RAWs.");
        report("Checking RAW cache", "Reading the original RAW fingerprint for reusable decoded input.");
        string identity = FileIdentity(original);
        string published = await RawCacheDirectoryAsync(original, proxy, text, token).ConfigureAwait(false);
        Decoded? metadata = null;
        if (!proxy)
        {
            string proxyDirectory = await RawCacheDirectoryAsync(original, true, text, token).ConfigureAwait(false);
            // Stay inside the current queue item: awaiting a queued proxy here would deadlock the decode queue.
            metadata = Directory.Exists(proxyDirectory)
                ? await ReadCacheAsync(proxyDirectory, true, token).ConfigureAwait(false)
                : (await DecodeRawAsync(original, true, text, report, token).ConfigureAwait(false)).Decode;
        }
        if (Directory.Exists(published))
        {
            report("Loading cached RAW", "Checking and reading the previously decoded linear image.");
            var decoded = await ReadCacheAsync(published, proxy, token, metadata?.Receipt).ConfigureAwait(false);
            return (decoded, ReadDecoded(decoded, proxy, token), metadata);
        }
        string job = Path.Combine(cache, "decode-" + Guid.NewGuid().ToString("N") + ".partial");
        Directory.CreateDirectory(job);
        try
        {
            report("Preparing RAW", "Copying the RAW into a private processing job.");
            File.Copy(original, Path.Combine(job, "input.RAF"));
            if (FileIdentity(original) != identity)
                throw new InvalidDataException("The camera input changed while preparing its decode.");
            CameraInputDecoder.WriteIdentityXmp(Path.Combine(job, "identity.xmp"), true);
            report("Decoding RAW", proxy ? "darktable is decoding a preview-resolution sensor image." :
                "darktable is decoding the full-resolution sensor image. The first conversion can take a while.");
            string extension = proxy ? ".tif" : ".pfm";
            string log = await CameraInputDecoder.ConvertAsync(cli, job, "input.RAF", "decoded" + extension, token,
                proxy ? ProxyEdge : null).ConfigureAwait(false);
            report("Validating decoded RAW", proxy ? "Checking exposure metadata, color profile and linear samples." :
                "Checking full-resolution dimensions, proxy exposure and finite linear samples.");
            CameraInputDecoder.ValidateHistory(log, await File.ReadAllTextAsync(Path.Combine(job, "decode.stderr.log"), token).ConfigureAwait(false), true);
            string output = Path.Combine(job, "decoded" + extension);
            Exposure physical;
            LinearImage image;
            if (proxy)
            {
                physical = ReadExposure(output);
                CameraInputDecoder.ValidateFloatProfile(output);
                image = ProcessingImages.ReadLinear(output, token);
            }
            else
            {
                var verified = metadata ?? throw new InvalidDataException("Full-resolution camera decode requires a verified proxy receipt.");
                physical = verified.Receipt.Exposure;
                image = ReadPfm(output, token);
                ValidateFullDimensions(image.Width, image.Height, verified.Receipt);
                ValidateBackingFiles([verified]);
            }
            if (FileIdentity(original) != identity)
                throw new InvalidDataException("The camera input changed while decoding.");
            string result = Path.Combine(job, "linear" + extension);
            report("Saving RAW cache", "Publishing validated linear input for later previews.");
            // Publish darktable's validated pixels unchanged; only the proxy TIFF carries ICC and EXIF.
            File.Move(output, result);
            var info = new FileInfo(result);
            var receipt = new Receipt(image.Width, image.Height, physical, null, info.Length, info.LastWriteTimeUtc.Ticks);
            await File.WriteAllTextAsync(Path.Combine(job, "receipt.json"), JsonSerializer.Serialize(receipt), token).ConfigureAwait(false);
            File.Delete(Path.Combine(job, "input.RAF"));
            token.ThrowIfCancellationRequested();
            try { Directory.Move(job, published); }
            catch (IOException) when (Directory.Exists(published))
            {
                Directory.Delete(job, true);
                var decoded = await ReadCacheAsync(published, proxy, token, metadata?.Receipt).ConfigureAwait(false);
                return (decoded, ReadDecoded(decoded, proxy, token), metadata);
            }
            return (new(Path.Combine(published, "linear" + extension), receipt), image, metadata);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException
            or NotSupportedException or TimeoutException or System.ComponentModel.Win32Exception or JsonException)
        {
            string failed = Path.ChangeExtension(job, ".failed");
            await File.WriteAllTextAsync(Path.Combine(job, "failure.txt"), error.ToString(), CancellationToken.None).ConfigureAwait(false);
            Directory.Move(job, failed);
            TrimFailures();
            if (error is OperationCanceledException) throw;
            throw new InvalidDataException($"RAF decode failed; no qualified input was published. {CameraInstructions} " +
                $"Inspect logs at {failed}. {error.Message}", error);
        }
    }

    private static async Task<Decoded> ReadCacheAsync(string directory, bool proxy, CancellationToken token,
        Receipt? metadata = null)
    {
        Receipt receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<Receipt>(await File.ReadAllTextAsync(Path.Combine(directory, "receipt.json"), token).ConfigureAwait(false))
                ?? throw new InvalidDataException($"Invalid decode cache receipt: {directory}");
        }
        catch (JsonException error) { throw new InvalidDataException($"Invalid decode cache receipt: {directory}", error); }
        string path = Path.Combine(directory, proxy ? "linear.tif" : "linear.pfm");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != receipt.Length || info.LastWriteTimeUtc.Ticks != receipt.LastWriteUtcTicks)
            throw new InvalidDataException($"Corrupt camera decode cache; remove this cache entry and retry: {directory}");
        if (receipt.Exposure is null || !receipt.Exposure.IsValid)
            throw new InvalidDataException($"Invalid camera decode cache dimensions/exposure: {directory}");
        if (proxy)
        {
            CameraInputDecoder.ValidateFloatProfile(path);
            using var tif = Tiff.Open(path, "r") ?? throw new InvalidDataException($"Missing decoded TIFF: {path}");
            if (tif.GetField(TiffTag.IMAGEWIDTH)?[0].ToInt() != receipt.Width ||
                tif.GetField(TiffTag.IMAGELENGTH)?[0].ToInt() != receipt.Height)
                throw new InvalidDataException($"Invalid camera decode cache dimensions/exposure: {directory}");
        }
        else
        {
            if (metadata is null || !receipt.Exposure.Matches(metadata.Exposure))
                throw new InvalidDataException($"Invalid camera decode cache exposure: {directory}");
            ValidateFullDimensions(receipt.Width, receipt.Height, metadata);
        }
        return new(path, receipt);
    }

    private static LinearImage ReadDecoded(Decoded decoded, bool proxy, CancellationToken token) =>
        proxy ? ProcessingImages.ReadLinear(decoded.Path, token) : ReadPfm(decoded.Path, token, decoded.Receipt);

    private static void ValidateFullDimensions(int width, int height, Receipt proxy)
    {
        if (width < proxy.Width || height < proxy.Height || width <= 0 || height <= 0 ||
            (long)width * height > 60_000_000 ||
            Math.Abs((long)width * proxy.Height - (long)height * proxy.Width) > Math.Max(width, height))
            throw new InvalidDataException("Full-resolution camera decode dimensions/aspect do not match the proxy.");
    }

    private static LinearImage ReadPfm(string path, CancellationToken token, Receipt? receipt = null)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        string Line()
        {
            var line = new StringBuilder();
            for (int i = 0; i < 80; i++)
            {
                int c = file.ReadByte();
                if (c == '\n') return line.ToString();
                if (c < 0 || c > 127) break;
                line.Append((char)c);
            }
            throw new InvalidDataException($"Invalid camera PFM header: {path}");
        }
        if (Line() != "PF") throw new InvalidDataException($"Expected RGB float32 PFM: {path}");
        string[] dimensions = Line().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (dimensions.Length != 2 ||
            !int.TryParse(dimensions[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width) ||
            !int.TryParse(dimensions[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height) ||
            width <= 0 || height <= 0 || (long)width * height > 60_000_000 ||
            (receipt is not null && (width != receipt.Width || height != receipt.Height)) ||
            !double.TryParse(Line(), NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) ||
            scale != -1 || !BitConverter.IsLittleEndian ||
            file.Length - file.Position != (long)width * height * 12)
            throw new InvalidDataException($"Invalid camera PFM dimensions, scale or payload: {path}");
        var pixels = new float[checked(width * height * 3)];
        var limit = new Vector<float>(float.MaxValue);
        for (int y = height - 1; y >= 0; y--)
        {
            token.ThrowIfCancellationRequested();
            var row = pixels.AsSpan(y * width * 3, width * 3);
            // PFM stores bottom-to-top native float RGB; read directly into the final top-left row.
            file.ReadExactly(MemoryMarshal.AsBytes(row));
            int i = 0;
            if (Vector.IsHardwareAccelerated)
                for (; i <= row.Length - Vector<float>.Count; i += Vector<float>.Count)
                    if (!Vector.LessThanOrEqualAll(Vector.Abs(new Vector<float>(row.Slice(i, Vector<float>.Count))), limit))
                        throw new InvalidDataException($"Invalid linear sample in {path}, row {y}.");
            for (; i < row.Length; i++)
                if (!float.IsFinite(row[i])) throw new InvalidDataException($"Invalid linear sample in {path}, row {y}.");
        }
        return new(width, height, pixels);
    }

    private void TrimFailures()
    {
        foreach (var directory in new DirectoryInfo(cache).GetDirectories("decode-*.failed")
            .OrderByDescending(d => d.LastWriteTimeUtc).Skip(10))
            directory.Delete(true);
    }

    private static string FileIdentity(string path)
    {
        var file = new FileInfo(path);
        return file.FullName + ":" + file.Length + ":" + file.LastWriteTimeUtc.Ticks;
    }

    private async Task<string> RawCacheDirectoryAsync(string original, bool proxy, string text, CancellationToken token)
    {
        string identity = FileIdentity(original);
        var fingerprint = Fingerprints.GetOrAdd(identity, _ => new(() => HashOriginalAsync(original, CancellationToken.None)));
        string hash;
        try { hash = await fingerprint.Value.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            if (fingerprint.IsValueCreated && fingerprint.Value.IsFaulted) Fingerprints.TryRemove(identity, out _);
            throw;
        }
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Settings + "\n" + text + "\n" + hash + (proxy ? "\nproxy-2560-hq" : "\nfull"))));
        return Path.Combine(cache, key);
    }

    private static string CaptureIdentity(RollCapture capture, CaptureFile[] files) =>
        capture.Id + ":" + capture.Mode + ":" + capture.IsDemo + ":" +
        string.Join("|", files.Select(f => f.Channel + ":" + FileIdentity(f.Path)));

    private static void ValidateBackingFiles(Decoded[] decodes)
    {
        foreach (var decoded in decodes)
        {
            var file = new FileInfo(decoded.Path);
            if (!file.Exists || file.Length != decoded.Receipt.Length || file.LastWriteTimeUtc.Ticks != decoded.Receipt.LastWriteUtcTicks)
                throw new InvalidDataException($"Corrupt camera decode cache; remove this cache entry and retry: {file.DirectoryName}");
        }
    }

    private static async Task<string> HashOriginalAsync(string path, CancellationToken token) =>
        Convert.ToHexString(await HashFileAsync(path, token).ConfigureAwait(false));

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(file, token).ConfigureAwait(false);
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
            if (files.Length == 1 && (IsRaw(files[0].Path) || IsTiff(files[0].Path))) yield return files[0];
            else if (files.Length == 2 && files.Count(f => IsRaw(f.Path)) == 1 && files.Count(f => IsJpeg(f.Path)) == 1)
                yield return files.Single(f => IsRaw(f.Path));
            else throw new InvalidDataException($"{CaptureLabel(capture)}, {channel}: require one qualified TIFF, one RAF, or a RAF+JPEG pair. " +
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

    private static Exposure ReadExposure(string path)
    {
        using var tif = Tiff.Open(path, "r") ?? throw new InvalidDataException("Cannot read decoded exposure metadata.");
        string make = tif.GetField(TiffTag.MAKE)?[0].ToString() ?? "", model = tif.GetField(TiffTag.MODEL)?[0].ToString() ?? "";
        long offset = tif.GetField(TiffTag.EXIFIFD)?[0].ToLong() ?? 0;
        if (offset <= 0 || !tif.ReadEXIFDirectory(offset))
            throw new InvalidDataException("RAW decode lacks EXIF exposure metadata; matching physical exposures cannot be verified.");
        short[] sensitivity = tif.GetField(TiffTag.EXIF_ISOSPEEDRATINGS)?[1].ToShortArray() ?? [];
        var exposure = new Exposure(make, model, tif.GetField(TiffTag.EXIF_EXPOSURETIME)?[0].ToDouble() ?? 0,
            tif.GetField(TiffTag.EXIF_FNUMBER)?[0].ToDouble() ?? 0, sensitivity.Length == 1 ? (ushort)sensitivity[0] : 0);
        if (!exposure.IsValid || !make.Contains("FUJI", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected Fujifilm RAW with camera model, shutter speed, aperture and ISO metadata.");
        return exposure;
    }

    private static LinearImage AreaAverage(LinearImage image, int edge, CancellationToken token)
    {
        double scale = Math.Min(1, edge / (double)Math.Max(image.Width, image.Height));
        if (scale == 1) return image;
        int width = Math.Max(1, (int)(image.Width * scale)), height = Math.Max(1, (int)(image.Height * scale));
        var result = new LinearImage(width, height, new float[width * height * 3]);
        double sx = image.Width / (double)width, sy = image.Height / (double)height;
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                double left = x * sx, right = (x + 1) * sx, top = y * sy, bottom = (y + 1) * sy;
                double r = 0, g = 0, b = 0;
                for (int py = (int)top; py < Math.Min(image.Height, (int)Math.Ceiling(bottom)); py++)
                for (int px = (int)left; px < Math.Min(image.Width, (int)Math.Ceiling(right)); px++)
                {
                    double weight = (Math.Min(px + 1, right) - Math.Max(px, left)) * (Math.Min(py + 1, bottom) - Math.Max(py, top));
                    int i = (py * image.Width + px) * 3;
                    r += image.Pixels[i] * weight; g += image.Pixels[i + 1] * weight; b += image.Pixels[i + 2] * weight;
                }
                int output = (y * width + x) * 3;
                result.Pixels[output] = (float)(r / (sx * sy));
                result.Pixels[output + 1] = (float)(g / (sx * sy));
                result.Pixels[output + 2] = (float)(b / (sx * sy));
            }
        }
        return result;
    }

    public void Dispose()
    {
        Task? running, checking;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            running = drain;
            checking = version;
        }
        running?.GetAwaiter().GetResult();
        if (checking is not null)
        {
            try { checking.GetAwaiter().GetResult(); }
            catch (Exception error) when (error is OperationCanceledException or IOException or NotSupportedException
                or UnauthorizedAccessException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                System.Diagnostics.Trace.TraceInformation($"Frame source version check ended during disposal: {error.Message}");
            }
        }
        lock (gate)
        {
            images.Clear();
            exposures.Clear();
            proxies.Clear();
        }
        lifetime.Dispose();
    }
}
