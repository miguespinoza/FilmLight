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
    internal const string SettingsSavedMessage =
        "Settings saved for this frame. Export uses these settings; refresh the preview to inspect the result.";
    private readonly RollStore store;
    private readonly string? darktableCliPath;
    private readonly Func<FrameSources, IFrameProcessor>? processorFactory;
    private readonly Dictionary<Guid, DevelopRecipe> drafts = [];
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
        currentDraft = store.Document.Recipes.LastOrDefault(r => r.FrameId is null)
            ?? new DevelopRecipe { FilmKind = store.Document.FilmKind };
    }

    public string PositiveRoot => Path.Combine(store.RootPath, store.Document.IsDemo ? "SYNTHETIC-Positives" : "Positives");

    public void Prefetch(Guid frameId) => Sources.Prefetch(store.Document, Frame(frameId));

    /// <summary>A null frame retains the initial roll controls before the first capture.</summary>
    public DevelopRecipe Draft(Guid? frameId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frameId is not { } id) return currentDraft;
        var frame = Frame(id);
        if (!drafts.TryGetValue(id, out var draft))
        {
            draft = store.Document.Recipes.LastOrDefault(r => r.FrameId == id)
                ?? new DevelopRecipe { FilmKind = store.Document.FilmKind, FrameId = id, InheritControls = true };
            drafts.Add(id, draft);
        }
        if (draft.InheritControls && store.Document.Captures.LastOrDefault(c =>
            c.Purpose == CapturePurpose.Frame && c.Number == frame.Number - 1) is { } previous)
        {
            var source = Draft(previous.Id);
            if (draft.ControlsSourceRecipeId != source.Id || draft.ControlsSourceRevision != source.Revision)
                drafts[id] = draft = store.SaveRecipe(draft.WithControlsFrom(source) with
                {
                    ControlsSourceRecipeId = source.Id, ControlsSourceRevision = source.Revision
                });
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
            return currentDraft = store.SaveRecipe(changed);
        }
        var current = Draft(id);
        var local = changed.ForFrame(id);
        if (current.WithControlsFrom(local) != current)
            local = local with { InheritControls = false, ControlsSourceRecipeId = null, ControlsSourceRevision = 0 };
        var saved = store.SaveRecipe(local);
        return currentDraft = drafts[id] = saved;
    }

    public async Task<FramePreview> PreviewAsync(Guid frameId, IProgress<ProcessingProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var frame = Frame(frameId);
        var draft = Draft(frameId);
        progress?.Report(new(frameId, "Starting positive preview", "Preparing the current recipe and saved input."));
        if (draft.BlackPoint >= draft.WhitePoint)
            throw new InvalidOperationException("Black point must be less than white point.");
        if (draft.XmpTemplatePath is not null && !File.Exists(draft.XmpTemplatePath))
            throw new InvalidOperationException("The imported XMP is missing. Import it again before rendering.");
        var renderer = Processor;
        var prepared = await FrameCalibration.PrepareAsync(Sources, store.Document, frame, draft,
            Path.Combine(store.RootPath, "Working"), token, progress);
        if (prepared.Recipe != draft || !store.Document.Recipes.Contains(draft))
            draft = EditAndReport(frameId, prepared.Recipe, progress);
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
        return new(frameId, draft, xmpHash, result.ImagePath, result.SourceImagePath,
            new[] { prepared.Warning }.OfType<string>().Concat(result.Warnings),
            "Preview rendered for the exact current recipe. " + (ready
                ? "Settings are saved for this frame and used for export."
                : $"This frame is uncalibrated. Auto-pick again or pick {RequiredPicks(draft)}; export retains the warning."));
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
            + ". Settings saved for this frame; manual pickers can override.";
        progress?.Report(new(frameId, "Picture picks saved", sampling));
        var preview = await PreviewAsync(frameId, progress, token);
        return new(frameId, preview.Recipe, preview.XmpHash, preview.PositivePath, preview.SourcePath,
            preview.Warnings, $"Automatic picks saved for frame {frame.Number} only. Other frames keep their own calibration.", sampling);
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
            message = "Preview rendered for the exact current recipe. " +
                (changed.PictureCalibration?.Light?.IsClipped == true
                    ? "Picks saved for this frame only; clipped backlight is marker-only, with WB correction skipped."
                    : "Light and film picks are applied to this frame only.");
        }
        return new(frameId, Draft(frameId), preview.XmpHash, preview.PositivePath,
            preview.SourcePath, preview.Warnings, message, sampling);
    }

    public async Task<ExportSummary> ExportAsync(IReadOnlyList<Guid> frameIds,
        IProgress<ProcessingProgress>? progress, CancellationToken token, IProgress<ExportProgress>? exportProgress = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var renderer = Processor;
        var candidates = frameIds.Distinct().Select(Frame).OrderBy(c => c.Number).ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("Select a captured frame.");
        var completed = 0;
        var failed = 0;
        var warnings = new List<string>();
        foreach (var frame in candidates)
        {
            token.ThrowIfCancellationRequested();
            var recipe = Draft(frame.Id);
            if (!store.Document.Recipes.Contains(recipe)) recipe = Edit(frame.Id, recipe);
            var outputDirectory = Path.Combine(store.RootPath, "Processing");
            FrameExport Record(ExportState state, IReadOnlyList<string> files, string? error) =>
                new(frame.Id, recipe.Id, recipe.Revision, state, files, error, DateTimeOffset.UtcNow);
            try
            {
                var prepared = await FrameCalibration.PrepareAsync(Sources, store.Document, frame,
                    recipe, Path.Combine(store.RootPath, "Working"), token, progress);
                if (prepared.Warning is { } warning) warnings.Add(warning);
                if (prepared.Recipe != recipe) recipe = Edit(frame.Id, prepared.Recipe);
                var previous = store.Document.Exports.LastOrDefault(e => e.FrameId == frame.Id
                    && e.RecipeId == recipe.Id && e.RecipeRevision == recipe.Revision);
                if (previous?.State == ExportState.Complete)
                {
                    if (ValidOutputs(previous.OutputFiles))
                    {
                        completed++;
                        continue;
                    }
                    store.RecordExport(Record(ExportState.Failed, [],
                        "Saved outputs are missing or empty; regenerating in a new folder."));
                }
                store.RecordExport(Record(ExportState.Processing, [], null));
                exportProgress?.Report(new("Starting frame export", $"Preparing saved frame {frame.Number}.",
                    $"Batch: {completed}/{candidates.Length} complete, {failed} failed. Exporting frame {frame.Number} with saved revision {recipe.Revision}.",
                    $"Exporting frame {frame.Number}; saved revision {recipe.Revision}; {completed}/{candidates.Length} complete."));
                Directory.CreateDirectory(outputDirectory);
                var result = await renderer.ExportAsync(store.Document, frame, recipe, outputDirectory, progress, token);
                token.ThrowIfCancellationRequested();
                if (!ValidOutputs(result.OutputFiles))
                    throw new InvalidDataException("Processor did not produce both a nonempty TIFF and JPG.");
                exportProgress?.Report(new("Publishing positives", $"Moving validated frame {frame.Number} output into the positives folder."));
                var positives = PublishPositives(frame, recipe, result.OutputFiles, token);
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
            $"Batch finished: {completed}/{candidates.Length} complete, {failed} failed. Each frame used its own latest saved calibration/settings. Inputs retained; retry missing/failed jobs explicitly.");
    }

    private DevelopRecipe EditAndReport(Guid frameId, DevelopRecipe recipe, IProgress<ProcessingProgress>? progress)
    {
        var saved = Edit(frameId, recipe);
        progress?.Report(new(frameId, "Settings saved", SettingsSavedMessage));
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
        drafts.Clear();
    }
}
