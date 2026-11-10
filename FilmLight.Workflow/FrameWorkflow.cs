using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace FilmLight.Workflow;

public sealed class FramePreview
{
    internal FramePreview(Guid frameId, DevelopRecipe recipe, string? xmpHash, string positivePath,
        string? sourcePath, IEnumerable<string> warnings, string message, string? samplingMessage = null)
    {
        FrameId = frameId;
        Recipe = recipe;
        XmpHash = xmpHash;
        PositivePath = positivePath;
        SourcePath = sourcePath;
        Warnings = Array.AsReadOnly(warnings.ToArray());
        Message = message;
        SamplingMessage = samplingMessage;
    }

    public Guid FrameId { get; }
    public DevelopRecipe Recipe { get; }
    public string? XmpHash { get; }
    public string PositivePath { get; }
    public string? SourcePath { get; }
    public IReadOnlyList<string> Warnings { get; }
    public bool PicksReady => Recipe.PictureCalibration?.IsComplete(Recipe.FilmKind) == true;
    public string Message { get; }
    public string? SamplingMessage { get; }
}

public sealed record ExportSummary(int Completed, int Failed, IReadOnlyList<string> Warnings, string Message);
public sealed record ExportProgress(string? ActivityStage = null, string? ActivityDetail = null,
    string? Context = null, string? Message = null, IReadOnlyList<string>? Warnings = null);

// Calls are serialized by the owner; cancellation does not dispose the roll's reusable input cache.
[SupportedOSPlatform("windows")]
public sealed class FrameWorkflow : IDisposable
{
    internal const string UnacceptedEditsMessage =
        "Unaccepted edits. Render Preview again before acceptance. Export still uses only a previously accepted revision.";
    private readonly RollStore store;
    private readonly string? darktableCliPath;
    private readonly Func<FrameSources, IFrameProcessor>? processorFactory;
    private readonly Dictionary<Guid, DevelopRecipe> drafts = [];
    private readonly Dictionary<Guid, FramePreview> previews = [];
    private DevelopRecipe currentDraft;
    private FrameSources? sources;
    private IFrameProcessor? processor;
    private bool disposed;

    public FrameWorkflow(RollStore store, string? darktableCliPath) : this(store, darktableCliPath, null) { }

    internal FrameWorkflow(RollStore store, string? darktableCliPath,
        Func<FrameSources, IFrameProcessor>? processorFactory)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.darktableCliPath = darktableCliPath;
        this.processorFactory = processorFactory;
        currentDraft = Unaccepted(store.Document.Recipes.LastOrDefault()
            ?? new DevelopRecipe { FilmKind = store.Document.FilmKind });
    }

    public string PositiveRoot => Path.Combine(store.RootPath, store.Document.IsDemo ? "SYNTHETIC-Positives" : "Positives");

    public DevelopRecipe? AcceptedRecipe(Guid? frameId) =>
        store.Document.Recipes.LastOrDefault(r => r.Accepted && r.FrameId == frameId);

    public DevelopRecipe? AcceptedSettings() => store.Document.Recipes.LastOrDefault(r => r.Accepted);

    public void Prefetch(Guid frameId) => Sources.Prefetch(store.Document, Frame(frameId));

    /// <summary>A null frame retains the initial roll controls before the first capture.</summary>
    public DevelopRecipe Draft(Guid? frameId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frameId is not { } id) return currentDraft;
        _ = Frame(id);
        if (!drafts.TryGetValue(id, out var draft))
        {
            draft = Unaccepted(store.Document.Recipes.LastOrDefault(r => r.FrameId == id)
                ?? currentDraft.ForFrame(id));
            drafts.Add(id, draft);
        }
        return currentDraft = draft;
    }

    public DevelopRecipe Edit(Guid? frameId, DevelopRecipe changed)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(changed);
        if (frameId is not { } id)
        {
            if (changed.FrameId.HasValue)
                throw new InvalidOperationException("Select a captured frame first.");
            return currentDraft = store.SaveRecipe(Unaccepted(changed), false);
        }
        _ = Frame(id);
        var saved = store.SaveRecipe(Unaccepted(changed.ForFrame(id)), false);
        previews.Remove(id);
        return currentDraft = drafts[id] = saved;
    }

    public async Task<FramePreview> PreviewAsync(Guid frameId, IProgress<ProcessingProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var frame = Frame(frameId);
        var draft = Draft(frameId);
        previews.Remove(frameId);
        progress?.Report(new(frameId, "Starting positive preview", "Preparing the current recipe and saved input."));
        if (draft.BlackPoint >= draft.WhitePoint)
            throw new InvalidOperationException("Black point must be less than white point.");
        if (draft.XmpTemplatePath is not null && !File.Exists(draft.XmpTemplatePath))
            throw new InvalidOperationException("The imported XMP is missing. Import it again before rendering.");
        var renderer = Processor;
        var prepared = await FrameCalibration.PrepareAsync(Sources, store.Document, frame, draft,
            Path.Combine(store.RootPath, "Working"), token, progress);
        if (prepared.Recipe != draft) draft = EditAndReport(frameId, prepared.Recipe, progress);
        var xmpHash = XmpHash(draft);
        var result = await renderer.PreviewAsync(store.Document, frame, draft,
            Path.Combine(store.RootPath, "Previews"), progress, token);
        token.ThrowIfCancellationRequested();
        if (XmpHash(draft) != xmpHash)
            throw new InvalidDataException("The imported XMP changed during preview. Import a stable calibrated XMP and render again.");
        if (!File.Exists(result.ImagePath) || new FileInfo(result.ImagePath).Length == 0
            || !new[] { ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(result.ImagePath).ToLowerInvariant()))
            throw new InvalidDataException("Processor did not return an existing JPG/PNG preview.");
        var ready = draft.PictureCalibration?.IsComplete(draft.FilmKind) == true;
        return Remember(new(frameId, draft, xmpHash, result.ImagePath, result.SourceImagePath,
            new[] { prepared.Warning }.OfType<string>().Concat(result.Warnings),
            "Preview rendered for the exact current recipe. " + (ready
                ? "Inspect it, then accept; any manual edit requires another preview."
                : $"This frame is uncalibrated. Auto-pick again or pick {RequiredPicks(draft)}; accepting/exporting retains the warning.")));
    }

    public async Task<FramePreview> AutoPickAsync(Guid frameId, IProgress<ProcessingProgress>? progress, CancellationToken token)
    {
        var frame = Frame(frameId);
        _ = Processor;
        var changed = await FrameCalibration.AutoSampleAsync(Sources, store.Document, frame, Draft(frameId),
            Path.Combine(store.RootPath, "Working"), token, progress);
        EditAndReport(frameId, changed, progress);
        var picks = changed.PictureCalibration!;
        static string Rgb(PictureSample sample) => $"({sample.Red:F3}, {sample.Green:F3}, {sample.Blue:F3})";
        var sampling = $"Automatic picks from frame {frame.Number} (linear RGB): light {Rgb(picks.Light!)}"
            + (picks.FilmBase is { } basis ? $"; film {Rgb(basis)}" : "")
            + ". Inspect the positive, then accept; manual pickers can override.";
        progress?.Report(new(frameId, "Picture picks saved", sampling));
        var preview = await PreviewAsync(frameId, progress, token);
        return Remember(new(frameId, preview.Recipe, preview.XmpHash, preview.PositivePath, preview.SourcePath,
            preview.Warnings, $"Automatic picks saved for frame {frame.Number} only. Other frames keep their own calibration.", sampling));
    }

    public async Task<FramePreview> PickAsync(Guid frameId, CalibrationTarget target, double x, double y,
        IProgress<ProcessingProgress>? progress, CancellationToken token)
    {
        var frame = Frame(frameId);
        _ = Processor;
        var changed = await FrameCalibration.SampleAsync(Sources, store.Document, frame, Draft(frameId),
            target, x, y, token, progress);
        EditAndReport(frameId, changed, progress);
        var sampling = $"{target} sampled from frame {frame.Number} at {x:P0}, {y:P0}. Saved for this frame only.";
        if (changed.PictureCalibration?.Light?.IsClipped == true)
            sampling += " Backlight is clipped: marker saved, WB correction skipped.";
        progress?.Report(new(frameId, "Picture picks saved", sampling));
        var preview = await PreviewAsync(frameId, progress, token);
        var message = preview.Message;
        if (preview.PicksReady)
        {
            token.ThrowIfCancellationRequested();
            Accept(preview);
            message = "Preview rendered for the exact current recipe. " +
                (changed.PictureCalibration?.Light?.IsClipped == true
                    ? "Picks saved for this frame only; clipped backlight is marker-only, with WB correction skipped."
                    : "Light and film picks are applied to this frame only.");
        }
        return Remember(new(frameId, Draft(frameId), preview.XmpHash, preview.PositivePath,
            preview.SourcePath, preview.Warnings, message, sampling));
    }

    public DevelopRecipe Accept(FramePreview preview)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(preview);
        if (!previews.TryGetValue(preview.FrameId, out var issued) || !ReferenceEquals(issued, preview)
            || Draft(preview.FrameId) != preview.Recipe)
            throw new InvalidOperationException("Render a successful preview of this exact recipe and selected frame first.");
        if (XmpHash(preview.Recipe) != preview.XmpHash)
        {
            previews.Remove(preview.FrameId);
            throw new InvalidOperationException("The imported XMP changed since preview. Render it again before acceptance.");
        }
        var accepted = store.SaveRecipe(preview.Recipe, true);
        currentDraft = drafts[preview.FrameId] = Unaccepted(accepted);
        previews.Remove(preview.FrameId);
        return accepted;
    }

    public async Task<ExportSummary> ExportAsync(IReadOnlyList<Guid> frameIds,
        IProgress<ProcessingProgress>? progress, CancellationToken token, IProgress<ExportProgress>? exportProgress = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (AcceptedSettings() is not { } approvedSettings)
            throw new InvalidOperationException("No accepted recipe. Render and accept a preview first.");
        var renderer = Processor;
        var candidates = frameIds.Distinct().Select(Frame).OrderBy(c => c.Number).ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("Select a captured frame.");
        var completed = 0;
        var failed = 0;
        var warnings = new List<string>();
        foreach (var frame in candidates)
        {
            token.ThrowIfCancellationRequested();
            var acceptedRecipe = AcceptedRecipe(frame.Id);
            if (acceptedRecipe is null)
            {
                var prepared = await FrameCalibration.PrepareAsync(Sources, store.Document, frame,
                    approvedSettings.ForFrame(frame.Id), Path.Combine(store.RootPath, "Working"), token, progress);
                if (prepared.Warning is { } warning) warnings.Add(warning);
                acceptedRecipe = store.SaveRecipe(prepared.Recipe, true);
                currentDraft = drafts[frame.Id] = Unaccepted(acceptedRecipe);
                previews.Remove(frame.Id);
            }
            var previous = store.Document.Exports.LastOrDefault(e => e.FrameId == frame.Id
                && e.RecipeId == acceptedRecipe.Id && e.RecipeRevision == acceptedRecipe.Revision);
            if (previous?.State == ExportState.Complete)
            {
                if (ValidOutputs(previous.OutputFiles))
                {
                    completed++;
                    continue;
                }
                store.RecordExport(new(frame.Id, acceptedRecipe.Id, acceptedRecipe.Revision, ExportState.Failed,
                    [], "Saved outputs are missing or empty; regenerating in a new folder.", DateTimeOffset.UtcNow));
            }
            var outputDirectory = Path.Combine(store.RootPath, "Processing");
            FrameExport Record(ExportState state, IReadOnlyList<string> files, string? error) =>
                new(frame.Id, acceptedRecipe.Id, acceptedRecipe.Revision, state, files, error, DateTimeOffset.UtcNow);
            store.RecordExport(Record(ExportState.Processing, [], null));
            exportProgress?.Report(new("Starting frame export", $"Preparing saved frame {frame.Number}.",
                $"Batch: {completed}/{candidates.Length} complete, {failed} failed. Exporting frame {frame.Number} with accepted revision {acceptedRecipe.Revision}.",
                $"Exporting frame {frame.Number}; accepted revision {acceptedRecipe.Revision}; {completed}/{candidates.Length} complete."));
            try
            {
                Directory.CreateDirectory(outputDirectory);
                var result = await renderer.ExportAsync(store.Document, frame, acceptedRecipe, outputDirectory, progress, token);
                token.ThrowIfCancellationRequested();
                if (!ValidOutputs(result.OutputFiles))
                    throw new InvalidDataException("Processor did not produce both a nonempty TIFF and JPG.");
                exportProgress?.Report(new("Publishing positives", $"Moving validated frame {frame.Number} output into the positives folder."));
                var positives = PublishPositives(frame, acceptedRecipe, result.OutputFiles, token);
                store.RecordExport(Record(ExportState.Complete, positives, null));
                try { PruneGeneratedImages(Path.GetDirectoryName(result.OutputFiles[0])!); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Frame {frame.Number}: positive outputs are saved, but generated working images could not be removed: {ex.Message}");
                }
                warnings.AddRange(result.Warnings.Select(w => $"Frame {frame.Number}: {w}"));
                completed++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                store.RecordExport(Record(ExportState.Canceled, [], "Canceled by operator; partial outputs and all inputs retained."));
                throw;
            }
            catch (Exception ex) when (IsOperationalError(ex))
            {
                store.RecordExport(Record(ExportState.Failed, [], ex.Message));
                failed++;
                warnings.Add($"Frame {frame.Number} FAILED: {ex.Message}");
            }
            exportProgress?.Report(new(Warnings: Array.AsReadOnly(warnings.ToArray())));
        }
        return new(completed, failed, warnings.AsReadOnly(),
            $"Batch finished: {completed}/{candidates.Length} complete, {failed} failed. Each frame used its own accepted calibration/settings. Inputs retained; retry missing/failed jobs explicitly.");
    }

    private FramePreview Remember(FramePreview preview) => previews[preview.FrameId] = preview;

    private DevelopRecipe EditAndReport(Guid frameId, DevelopRecipe recipe, IProgress<ProcessingProgress>? progress)
    {
        var saved = Edit(frameId, recipe);
        progress?.Report(new(frameId, "Unaccepted edits", UnacceptedEditsMessage));
        return saved;
    }

    private RollCapture Frame(Guid frameId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return store.Document.Captures.SingleOrDefault(c => c.Id == frameId && c.Purpose == CapturePurpose.Frame)
            ?? throw new InvalidOperationException("The selected frame must belong to this roll.");
    }

    internal FrameSources Sources
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return sources ??= new FrameSources(Path.Combine(store.RootPath, "Working", "camera-input-cache"),
                store.Document.IsDemo ? null : darktableCliPath);
        }
    }

    private IFrameProcessor Processor
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (processor is not null) return processor;
            if (processorFactory is not null) return processor = processorFactory(Sources);
            if (store.Document.IsDemo) return processor = new DemoFrameProcessor(Sources);
            if (string.IsNullOrWhiteSpace(darktableCliPath) || !File.Exists(darktableCliPath))
                throw new InvalidOperationException("Install darktable and choose darktable-cli.exe in Develop settings. Real captures never fall back to a demo processor.");
            return processor = new DarktableFrameProcessor(darktableCliPath, Sources);
        }
    }

    private static DevelopRecipe Unaccepted(DevelopRecipe recipe) => recipe with { Accepted = false, AcceptedAt = null };
    private static string RequiredPicks(DevelopRecipe recipe) => recipe.FilmKind == FilmKind.Positive ? "exposed light" : "exposed light and clear film";

    private static string? XmpHash(DevelopRecipe recipe)
    {
        if (recipe.XmpTemplatePath is null) return null;
        using var stream = File.OpenRead(recipe.XmpTemplatePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool ValidOutputs(IReadOnlyList<string> paths) =>
        paths.All(p => File.Exists(p) && new FileInfo(p).Length > 0)
        && paths.Any(p => new[] { ".tif", ".tiff" }.Contains(Path.GetExtension(p).ToLowerInvariant()))
        && paths.Any(p => new[] { ".jpg", ".jpeg" }.Contains(Path.GetExtension(p).ToLowerInvariant()));

    private void PruneGeneratedImages(string directory)
    {
        var processingRoot = Path.GetFullPath(Path.Combine(store.RootPath, "Processing")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(directory).StartsWith(processingRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Generated-image cleanup was outside the roll's processing folder.");
        foreach (var name in new[] { "normalized-linear.tif", "developed-linear.tif" })
            File.Delete(Path.Combine(directory, name));
    }

    private IReadOnlyList<string> PublishPositives(RollCapture frame, DevelopRecipe recipe,
        IReadOnlyList<string> files, CancellationToken token)
    {
        var staging = Path.Combine(store.RootPath, "Processing", $".publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var names = new List<string>();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".tif" or ".tiff" or ".jpg" or ".jpeg"))
                throw new InvalidDataException("Only positive TIFF/JPEG images may be published.");
            var name = $"{(store.Document.IsDemo ? "SYNTHETIC-" : "")}Frame-{frame.Number:D4}{extension}";
            File.Move(file, Path.Combine(staging, name));
            names.Add(name);
        }
        File.WriteAllText(Path.Combine(staging, "recipe.json"), JsonSerializer.Serialize(recipe));
        token.ThrowIfCancellationRequested();
        var parent = Path.Combine(PositiveRoot, $"r{recipe.Revision}-{recipe.Id.ToString("N")[..8]}");
        Directory.CreateDirectory(parent);
        var published = Path.Combine(parent, $"Frame-{frame.Number:D4}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.Move(staging, published);
        return names.Select(name => Path.Combine(published, name)).ToArray();
    }

    private static bool IsOperationalError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or JsonException
        or System.Runtime.InteropServices.ExternalException or System.ComponentModel.Win32Exception or TimeoutException
        or NotSupportedException;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        sources?.Dispose();
        previews.Clear();
        drafts.Clear();
    }
}
