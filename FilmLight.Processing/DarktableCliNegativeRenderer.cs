using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.RegularExpressions;
using BitMiracle.LibTiff.Classic;
using StbImageSharp;




namespace FilmLight.Processing;

public sealed class DarktableCliNegativeRenderer : INegativeRenderer, IAsyncDisposable
{
    private static readonly (int Major, int Minor)[] SupportedVersions = [(5, 6)];
    private readonly string? cliPath;
    private readonly string root;
    private readonly SemaphoreSlim interactive = new(1, 1);
    private readonly SemaphoreSlim export = new(1, 1);
    private readonly SemaphoreSlim probe = new(1, 1);
    private readonly object proofSync = new();
    private readonly CancellationTokenSource shutdown = new();
    private int disposed;
    private CancellationTokenSource? latestProof;
    private NegativeLabCapabilities? capabilities;
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromMinutes(15);

    public DarktableCliNegativeRenderer(string cliPath, string cacheRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cliPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRootPath);
        this.cliPath = Environment.GetEnvironmentVariable("FILMLIGHT_DARKTABLE_CLI") is { Length: > 0 } overridden ? overridden : cliPath;
        root = Path.Combine(Path.GetFullPath(cacheRootPath), "negative-lab", "work", Guid.NewGuid().ToString("N"));
    }

    public static string? DiscoverPath(string? configuredPath = null)
    {
        var overridden = Environment.GetEnvironmentVariable("FILMLIGHT_DARKTABLE_CLI");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
        if (!string.IsNullOrWhiteSpace(configuredPath)) return configuredPath;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var path = Path.Combine(programFiles, "darktable", "bin", OperatingSystem.IsWindows() ? "darktable-cli.exe" : "darktable-cli");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Reads negadoctor and normalized temperature coefficients, not the complete darktable pipeline.</summary>
    /// <remarks>Camera-reference temperature histories also apply a camera-dependent correction in colorin.
    /// That correction is not represented by a pinned Light Balance; its omission is reported in Light.Source.</remarks>
    public static ValueTask<(LightBalance Light, NegadoctorParameters Conversion)> ReadProfileAsync(
        string xmpPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DarktableHistory.Read(xmpPath));
    }

    public async ValueTask<NegativeLabCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        cancellationToken = lifetime.Token;
        await probe.WaitAsync(cancellationToken);
        try
        {
            if (capabilities is not null) return capabilities;
            if (!File.Exists(cliPath))
                return capabilities = new(false, cliPath, null, "darktable 5.6 was not found.", DarktableHistory.TemplateHash);
            try
            {
                var start = new ProcessStartInfo(cliPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--version");
                var text = await RunProcessAsync(start, TimeSpan.FromSeconds(30), cancellationToken);
                var match = Regex.Match(text, @"darktable\s+(\d+)\.(\d+)\.(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var version = match.Success ? $"{match.Groups[1]}.{match.Groups[2]}.{match.Groups[3]}" : null;
                bool supported = match.Success && SupportedVersions.Contains((int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)));
                return capabilities = new(supported, cliPath, version, supported ? null : $"Untested darktable version: {version ?? "unknown"}. Supported: 5.6.x.", DarktableHistory.TemplateHash);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return capabilities = new(false, cliPath, null, $"Could not probe darktable: {error.Message}", DarktableHistory.TemplateHash);
            }
        }
        finally { probe.Release(); }
    }

    public ValueTask RenderLinearAsync(NegativeRenderRequest request, string outputPath, CancellationToken cancellationToken) =>
        RenderAsync(request, outputPath, true, request.FullResolutionLinear, cancellationToken);

    public async ValueTask RenderProofAsync(NegativeRenderRequest request, string outputPath, CancellationToken cancellationToken)
    {
        using var current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (proofSync)
        {
            latestProof?.Cancel();
            latestProof = current;
        }
        try { await RenderAsync(request, outputPath, false, false, current.Token); }
        finally
        {
            lock (proofSync)
            {
                if (ReferenceEquals(latestProof, current)) latestProof = null;
            }
        }
    }

    public ValueTask RenderExportAsync(NegativeRenderRequest request, string outputPath, CancellationToken cancellationToken) =>
        RenderAsync(request, outputPath, false, true, cancellationToken);

    internal ValueTask RenderReferenceAsync(NegativeRenderRequest request, string outputPath, string xmpPath, bool linear, CancellationToken ct) =>
        RenderAsync(request, outputPath, linear, false, ct, xmpPath);

    private async ValueTask RenderAsync(NegativeRenderRequest request, string outputPath, bool linear, bool full,
        CancellationToken cancellationToken, string? referenceHistory = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        cancellationToken = lifetime.Token;
        ArgumentNullException.ThrowIfNull(request);
        NegativeConversionValidation.Validate(request.Conversion);
        ArgumentNullException.ThrowIfNull(request.Light);
        if (request.MaxEdge < 1 || request.MaxEdge > 32768) throw new ArgumentOutOfRangeException(nameof(request.MaxEdge));
        var available = await GetCapabilitiesAsync(cancellationToken);
        if (!available.IsSupported) throw new NotSupportedException(available.UnsupportedReason);
        var worker = full ? export : interactive;
        await worker.WaitAsync(cancellationToken);
        string workspace = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string markerPath = Path.Combine(workspace, NegativeCacheLayout.WorkspaceMarkerName);
        FileStream? workspaceLease = null;
        bool success = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(workspace);
            workspaceLease = new FileStream(markerPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Delete);
            var sourcePath = Path.GetFullPath(request.SourcePath);
            var source = new FileInfo(sourcePath);
            var originalSize = source.Length;
            var originalTime = source.LastWriteTimeUtc;
            string sourceName = "source" + Path.GetExtension(sourcePath);
            bool tiff = full || request.SixteenBitProof;
            string outputName = linear ? "result.pfm" : tiff ? "result.tif" : "result.jpg";
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous))
            await using (var copy = new FileStream(Path.Combine(workspace, sourceName), FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
                await input.CopyToAsync(copy, cancellationToken);
            if (referenceHistory is null) DarktableHistory.Write(Path.Combine(workspace, "history.xmp"), request.Light, request.Conversion, linear, request.CameraChannels);
            else File.Copy(referenceHistory, Path.Combine(workspace, "history.xmp"));
            string config = Path.Combine(root, full ? "export-config" : "interactive-config");
            Directory.CreateDirectory(config);
            var start = BuildStartInfo(cliPath!, workspace, config, sourceName, outputName, linear, full, request.MaxEdge, request.CameraChannels);
            try
            {
                var log = await RunProcessAsync(start, RenderTimeout, cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(workspace, "darktable.log"), log, CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
                var result = Path.Combine(workspace, outputName);
                ValidateOutput(result, linear, tiff, cancellationToken);
                source.Refresh();
                if (source.Length != originalSize || source.LastWriteTimeUtc != originalTime)
                    throw new IOException("The RAW changed during Negative Conversion. Prepare it again.");
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(result, destination, overwrite: false);
                success = true;
            }
            finally
            {
                source.Refresh();
                if (source.Length != originalSize || source.LastWriteTimeUtc != originalTime)
                    throw new IOException("The RAW changed during Negative Conversion. Prepare it again.");
            }
        }
        catch (OperationCanceledException error)
        {
            error.Data["NegativeConversionWorkspace"] = workspace;
            Trace.TraceWarning("Cancelled Negative Conversion workspace retained: {0}", workspace);
            throw;
        }
        catch (Exception error)
        {
            Trace.TraceError("Negative Conversion workspace retained: {0}", workspace);
            throw new IOException($"Negative Conversion failed. Workspace retained: {workspace}. {error.Message}", error);
        }
        finally
        {
            try { workspaceLease?.Dispose(); }
            finally
            {
                if (!success && workspaceLease is not null)
                {
                    try { File.SetLastWriteTimeUtc(markerPath, DateTime.UtcNow); }
                    catch (IOException error) { Trace.TraceWarning("Could not touch retained Negative Conversion workspace marker: {0}", error.Message); }
                    catch (UnauthorizedAccessException error) { Trace.TraceWarning("Could not touch retained Negative Conversion workspace marker: {0}", error.Message); }
                }
                worker.Release();
                if (success)
                {
                    try { Directory.Delete(workspace, recursive: true); }
                    catch (IOException error) { Trace.TraceWarning("Could not remove render workspace: {0}", error.Message); }
                    catch (UnauthorizedAccessException error) { Trace.TraceWarning("Could not remove render workspace: {0}", error.Message); }
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await shutdown.CancelAsync();
        await interactive.WaitAsync();
        await export.WaitAsync();
        await probe.WaitAsync();
        interactive.Release();
        export.Release();
        probe.Release();
        foreach (var config in new[] { "interactive-config", "export-config" })
        {
            try
            {
                var path = Path.Combine(root, config);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (IOException error) { Trace.TraceWarning("Could not remove darktable config: {0}", error.Message); }
            catch (UnauthorizedAccessException error) { Trace.TraceWarning("Could not remove darktable config: {0}", error.Message); }
        }
        try
        {
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch (IOException error) { Trace.TraceWarning("Could not remove render root: {0}", error.Message); }
        catch (UnauthorizedAccessException error) { Trace.TraceWarning("Could not remove render root: {0}", error.Message); }
    }

    internal static ProcessStartInfo BuildStartInfo(string cli, string workspace, string config, string source,
        string output, bool linear, bool full, int edge, bool cameraChannels = false)
    {
        var start = new ProcessStartInfo(cli)
        {
            WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { source, "history.xmp", output }) start.ArgumentList.Add(arg);
        if (!full)
            foreach (var arg in new[] { "--width", edge.ToString(System.Globalization.CultureInfo.InvariantCulture), "--height", edge.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(arg);
        foreach (var arg in new[] { "--icc-type", linear ? "LIN_REC2020" : "SRGB", "--apply-custom-presets", "false", "--core",
            "--configdir", config, "--library", ":memory:", "--disable-opencl", "--conf", "write_sidecar_files=never",
            "--conf", "plugins/imageio/format/jpeg/quality=95", "--conf", "plugins/imageio/format/tiff/bpp=16",
            "--conf", "plugins/darkroom/workflow=none",
            "--conf", "plugins/lighttable/export/high_quality_processing=true" })
            start.ArgumentList.Add(arg);
        start.Environment["OMP_NUM_THREADS"] = Math.Max(1, Environment.ProcessorCount - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (cameraChannels)
            foreach (var arg in new[] { "--conf", "plugins/lighttable/export/pixel_interpolator=bilinear",
                "--conf", "plugins/lighttable/export/pixel_interpolator_warp=bilinear" })
                start.ArgumentList.Add(arg);
        return start;
    }

    internal static async Task<string> RunProcessAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = start };
        var processLabel = Path.GetFileName(start.FileName);
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException($"{processLabel} could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        Exception? failure = null;
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException error)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            failure = cancellationToken.IsCancellationRequested ? error : new TimeoutException($"{processLabel} exceeded {timeout}.");
        }
        var log = await stdout + Environment.NewLine + await stderr;
        if (start.ArgumentList.Contains("--core"))
            await File.WriteAllTextAsync(Path.Combine(start.WorkingDirectory, "darktable.log"), log, CancellationToken.None);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (process.ExitCode != 0) throw new IOException($"{processLabel} exited {process.ExitCode}: {log[^Math.Min(log.Length, 6000)..]}");
        return log;
    }

    internal static (int Width, int Height) ValidateOutput(string path, bool linear, bool tiff, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dimensions = ValidateOutputStructure(path, linear, tiff);
        if (linear) return dimensions;
        try
        {
            if (tiff) DecodeTiff(path, dimensions, cancellationToken);
            else
            {
                using var stream = File.OpenRead(path);
                var decoded = ImageResult.FromStream(stream, ColorComponents.RedGreenBlue);
                if (decoded.Width != dimensions.Width || decoded.Height != dimensions.Height ||
                    decoded.Data.LongLength != (long)decoded.Width * decoded.Height * 3)
                    throw new InvalidDataException("JPEG decoded dimensions do not match its frame.");
            }
        }
        catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException))
        {
            throw new InvalidDataException($"darktable output could not be decoded: {Path.GetFileName(path)}.", error);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return dimensions;
    }

    private static void DecodeTiff(string path, (int Width, int Height) dimensions, CancellationToken cancellationToken)
    {
        using var image = Tiff.Open(path, "r") ?? throw new InvalidDataException("TIFF decoder could not open the image.");
        int width = image.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        int height = image.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        if (width != dimensions.Width || height != dimensions.Height)
            throw new InvalidDataException("TIFF decoded dimensions do not match its directory.");
        int planes = image.GetFieldDefaulted(TiffTag.PLANARCONFIG)[0].ToInt() == (int)PlanarConfig.SEPARATE
            ? image.GetFieldDefaulted(TiffTag.SAMPLESPERPIXEL)[0].ToInt() : 1;
        if (planes < 1 || planes > 16) throw new InvalidDataException("Invalid TIFF sample count.");
        if (image.IsTiled())
        {
            int tileWidth = image.GetField(TiffTag.TILEWIDTH)[0].ToInt();
            int tileHeight = image.GetField(TiffTag.TILELENGTH)[0].ToInt();
            int size = image.TileSize();
            if (tileWidth <= 0 || tileHeight <= 0 || size <= 0 || size > 256 * 1024 * 1024)
                throw new InvalidDataException("Invalid TIFF tile geometry.");
            var buffer = new byte[size];
            for (short plane = 0; plane < planes; plane++)
                for (int y = 0; y < height; y += tileHeight)
                    for (int x = 0; x < width; x += tileWidth)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (image.ReadTile(buffer, 0, x, y, 0, plane) < 0)
                            throw new InvalidDataException("TIFF tile could not be decoded.");
                    }
        }
        else
        {
            int size = image.ScanlineSize();
            if (size <= 0 || size > 256 * 1024 * 1024) throw new InvalidDataException("Invalid TIFF scanline size.");
            var buffer = new byte[size];
            for (short plane = 0; plane < planes; plane++)
                for (int y = 0; y < height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!image.ReadScanline(buffer, y, plane)) throw new InvalidDataException("TIFF scanline could not be decoded.");
                }
        }
    }

    private static (int Width, int Height) ValidateOutputStructure(string path, bool linear, bool tiff)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new InvalidDataException("darktable produced no image.");
        if (linear)
        {
            var size = PfmReader.Validate(path);
            return (size.Width, size.Height);
        }
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (tiff)
        {
            var order = reader.ReadUInt16();
            bool little = order == 0x4949;
            if (!little && order != 0x4d4d) throw new InvalidDataException("Invalid TIFF byte order.");
            ushort U16() { var b = reader.ReadBytes(2); if (b.Length != 2) throw new EndOfStreamException(); return little ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b); }
            uint U32() { var b = reader.ReadBytes(4); if (b.Length != 4) throw new EndOfStreamException(); return little ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b); }
            if (U16() != 42) throw new InvalidDataException("Invalid TIFF header.");
            stream.Position = U32();
            int entries = U16(), width = 0, height = 0;
            uint[]? offsets = null, byteCounts = null;
            for (int i = 0; i < entries; i++)
            {
                int tag = U16(), type = U16();
                uint count = U32(), value = U32();
                if ((tag == 256 || tag == 257) && count == 1 && (type == 3 || type == 4))
                {
                    int dimension = checked((int)(type == 3 ? little ? value & 0xffff : value >> 16 : value));
                    if (tag == 256) width = dimension; else height = dimension;
                }
                else if (tag is 273 or 279 or 324 or 325)
                {
                    if (count == 0 || count > 1_000_000 || type is not (3 or 4))
                        throw new InvalidDataException("Invalid TIFF strip/tile table.");
                    long resume = stream.Position;
                    int elementSize = type == 3 ? 2 : 4;
                    long tableSize = (long)count * elementSize;
                    if (tableSize > 4)
                    {
                        if (value + tableSize > stream.Length) throw new InvalidDataException("Truncated TIFF strip/tile table.");
                        stream.Position = value;
                    }
                    else stream.Position -= 4;
                    var values = new uint[count];
                    for (int n = 0; n < values.Length; n++) values[n] = type == 3 ? U16() : U32();
                    stream.Position = resume;
                    if (tag is 273 or 324) offsets = values; else byteCounts = values;
                }
            }
            if (width <= 0 || height <= 0 || offsets is null || byteCounts is null || offsets.Length != byteCounts.Length)
                throw new InvalidDataException("TIFF has no valid image dimensions or pixel storage.");
            for (int i = 0; i < offsets.Length; i++)
                if (offsets[i] < 8 || byteCounts[i] == 0 || (long)offsets[i] + byteCounts[i] > stream.Length)
                    throw new InvalidDataException("Truncated TIFF pixel storage.");
            return (width, height);
        }
        if (reader.ReadByte() != 0xff || reader.ReadByte() != 0xd8) throw new InvalidDataException("Invalid JPEG header.");
        int jpegWidth = 0, jpegHeight = 0;
        bool sawScan = false;
        bool quantization = false, huffman = false;
        int pendingMarker = -1;
        while (stream.Position < stream.Length)
        {
            int marker = pendingMarker;
            pendingMarker = -1;
            if (marker < 0)
            {
                if (reader.ReadByte() != 0xff) throw new InvalidDataException("Invalid JPEG marker.");
                do { marker = reader.ReadByte(); } while (marker == 0xff);
            }
            if (marker == 0xd9)
            {
                if (!sawScan || jpegWidth <= 0 || jpegHeight <= 0) throw new InvalidDataException("JPEG has no pixel scan.");
                return (jpegWidth, jpegHeight);
            }
            if (marker == 0x00 || marker == 0xd8 || marker is >= 0xd0 and <= 0xd7)
                throw new InvalidDataException("Invalid JPEG segment marker.");
            if (marker == 0x01) continue;
            int length = (reader.ReadByte() << 8) | reader.ReadByte();
            if (length < 2 || stream.Position + length - 2 > stream.Length) throw new InvalidDataException("Truncated JPEG.");
            long segmentEnd = stream.Position + length - 2;
            if (marker == 0xdb && length >= 67) quantization = true;
            if (marker == 0xc4 && length >= 19) huffman = true;
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (length < 8) throw new InvalidDataException("Invalid JPEG frame.");
                reader.ReadByte();
                jpegHeight = (reader.ReadByte() << 8) | reader.ReadByte();
                jpegWidth = (reader.ReadByte() << 8) | reader.ReadByte();
            }
            stream.Position = segmentEnd;
            if (marker == 0xda)
            {
                if (!quantization || !huffman) throw new InvalidDataException("JPEG has no usable coding tables.");
                int scanBytes = 0;
                while (stream.Position < stream.Length)
                {
                    if (reader.ReadByte() != 0xff) { scanBytes++; continue; }
                    int next;
                    do { next = reader.ReadByte(); } while (next == 0xff);
                    if (next == 0 || next is >= 0xd0 and <= 0xd7) { scanBytes++; continue; }
                    pendingMarker = next;
                    break;
                }
                if (scanBytes == 0 || pendingMarker < 0) throw new InvalidDataException("Truncated JPEG pixel scan.");
                sawScan = true;
            }
        }
        if (pendingMarker == 0xd9 && sawScan && jpegWidth > 0 && jpegHeight > 0) return (jpegWidth, jpegHeight);
        throw new InvalidDataException("JPEG has no complete pixel scan.");
    }
}

