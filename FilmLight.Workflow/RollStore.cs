using System.Text.Json;
using System.Text.Json.Serialization;
using FilmLight.Core;

namespace FilmLight.Workflow;

/// <summary>Persists roll metadata and camera-produced files; exposure control stays with the camera.</summary>
public sealed class RollStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    private readonly object gate = new();
    private RollDocument document;

    private RollStore(string rootPath, RollDocument document)
    {
        RootPath = rootPath;
        this.document = document;
    }

    public string RootPath { get; }

    public RollDocument Document
    {
        get
        {
            lock (gate)
                return Snapshot(document);
        }
    }

    public static RollStore Create(string rootPath, string name, FilmKind filmKind, bool isDemo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        CheckEnum(filmKind, nameof(filmKind));
        var root = LocalPath(rootPath);
        Directory.CreateDirectory(root);
        var store = new RollStore(root, new RollDocument
        {
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
            FilmKind = filmKind,
            IsDemo = isDemo
        });
        using var lease = store.AcquireLease();
        if (File.Exists(store.ManifestPath))
            throw new IOException("A roll already exists at this location.");
        store.Persist(store.document, create: true);
        return store;
    }

    public static RollStore Open(string rootPath)
    {
        var store = new RollStore(LocalPath(rootPath), new RollDocument());
        using var lease = store.AcquireLease();
        store.document = store.Read();
        return store;
    }

    public RollCapture AddCapture(CapturePurpose purpose, CaptureMode mode,
        IReadOnlyList<CaptureFile> files, bool isDemo)
    {
        CheckEnum(purpose, nameof(purpose));
        CheckEnum(mode, nameof(mode));
        ArgumentNullException.ThrowIfNull(files);
        var inputs = files.ToArray();
        ValidateChannels(mode, inputs);
        foreach (var file in inputs)
            RequireFile(LocalPath(file.Path));

        lock (gate)
        {
            using var lease = AcquireLease();
            var current = Read();
            if (isDemo != current.IsDemo)
                throw new InvalidOperationException("Real and demo captures cannot be mixed in a roll.");
            var id = Guid.NewGuid();
            var directory = Path.Combine(RootPath, "captures", id.ToString("N"));
            var ownedFiles = inputs.Select(file => new CaptureFile(file.Channel,
                CopyOwned(file.Path, directory, file.Channel.ToString() + Path.GetExtension(file.Path)))).ToArray();
            var capture = new RollCapture(id,
                purpose == CapturePurpose.Frame
                    ? checked(current.Captures.Count(c => c.Purpose == CapturePurpose.Frame) + 1) : 0,
                purpose, mode, ownedFiles, isDemo, DateTimeOffset.UtcNow);
            Commit(current with { Captures = [.. current.Captures, capture] });
            return capture with { Files = ownedFiles.ToArray() };
        }
    }

    public void SetPhase(WorkflowPhase phase)
    {
        CheckEnum(phase, nameof(phase));
        lock (gate)
        {
            using var lease = AcquireLease();
            var current = Read();
            ValidatePhase(current, phase);
            Commit(current with { Phase = phase });
        }
    }

    /// <summary>
    /// Appends a revision, never changing a saved revision. Crops are normalized edge margins;
    /// black/white points are normalized levels. XMP templates are copied, not linked.
    /// </summary>
    public DevelopRecipe SaveRecipe(DevelopRecipe draft, bool accept)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateRecipeValues(draft);
        lock (gate)
        {
            using var lease = AcquireLease();
            var current = Read();
            if (draft.FilmKind != current.FilmKind)
                throw new ArgumentException("Recipe film kind must match the roll.", nameof(draft));
            if (current.Recipes.Any(r => r.Id == draft.Id && r.FrameId != draft.FrameId))
                throw new InvalidOperationException("A recipe's frame cannot change. Create a separate recipe for the other frame.");
            if (accept)
                RequireCalibration(current, draft);
            var revision = checked(current.Recipes.Where(r => r.Id == draft.Id)
                .Select(r => r.Revision).DefaultIfEmpty(0).Max() + 1);
            var xmp = draft.XmpTemplatePath is null ? null
                : CopyOwned(draft.XmpTemplatePath,
                    Path.Combine(RootPath, "recipes", Guid.NewGuid().ToString("N")), "template.xmp");
            var saved = draft with
            {
                Revision = revision,
                XmpTemplatePath = xmp,
                Accepted = accept,
                AcceptedAt = accept ? DateTimeOffset.UtcNow : null
            };
            Commit(current with
            {
                Recipes = [.. current.Recipes, saved],
                AcceptedRecipeId = accept ? saved.Id : current.AcceptedRecipeId
            });
            return saved;
        }
    }

    /// <summary>Appends an export status, retaining failed/canceled attempts and their errors.</summary>
    public void RecordExport(FrameExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(export.OutputFiles);
        var saved = export with { OutputFiles = export.OutputFiles.Select(LocalPath).ToArray() };
        lock (gate)
        {
            using var lease = AcquireLease();
            var current = Read();
            ValidateExport(current, saved, requireOutputs: true);
            Commit(current with { Exports = [.. current.Exports, saved] });
        }
    }

    private string ManifestPath => Path.Combine(RootPath, "roll.json");

    // A persistent lock file serializes writers, including separate RollStore instances/processes.
    private FileStream AcquireLease()
    {
        LocalPath(RootPath);
        var path = LocalPath(Path.Combine(RootPath, ".roll.lock"));
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private void Commit(RollDocument next)
    {
        Persist(next, create: false);
        document = next;
    }

    private void Persist(RollDocument next, bool create)
    {
        ValidateDocument(next);
        LocalPath(ManifestPath);
        var temporary = Path.Combine(RootPath, $".roll-{Guid.NewGuid():N}.tmp");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, next, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        // Same-volume rename/replace publishes only a complete, flushed manifest. Failed writes
        // leave the previous manifest and any unrecorded ingest files intact for investigation.
        if (create)
            File.Move(temporary, ManifestPath);
        else
            File.Replace(temporary, ManifestPath, null);
    }

    private RollDocument Read()
    {
        try
        {
            using var stream = File.OpenRead(LocalPath(ManifestPath));
            using var json = JsonDocument.Parse(stream);
            RequireProperties<RollDocument>(json.RootElement, nameof(RollDocument.CreatedAt));
            foreach (var capture in json.RootElement.GetProperty(nameof(RollDocument.Captures)).EnumerateArray())
            {
                RequireProperties<RollCapture>(capture);
                foreach (var file in capture.GetProperty(nameof(RollCapture.Files)).EnumerateArray())
                    RequireProperties<CaptureFile>(file);
            }
            foreach (var recipe in json.RootElement.GetProperty(nameof(RollDocument.Recipes)).EnumerateArray())
                RequireProperties<DevelopRecipe>(recipe, nameof(DevelopRecipe.PictureCalibration), nameof(DevelopRecipe.FrameId));
            foreach (var export in json.RootElement.GetProperty(nameof(RollDocument.Exports)).EnumerateArray())
                RequireProperties<FrameExport>(export);
            var result = json.RootElement.Deserialize<RollDocument>(JsonOptions)
                ?? throw new InvalidDataException("Roll manifest is null.");
            ValidateDocument(result);
            return result;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException
            or InvalidOperationException or OverflowException)
        {
            throw new InvalidDataException("Invalid roll manifest.", exception);
        }
    }

    private static void RequireProperties<T>(JsonElement element, params string[] optionalProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Expected a {typeof(T).Name} object.");
        var names = element.EnumerateObject().Select(p => p.Name).ToArray();
        var expected = typeof(T).GetProperties().Select(p => p.Name).ToArray();
        if (names.Except(expected, StringComparer.Ordinal).Any() || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || expected.Any(name => !optionalProperties.Contains(name, StringComparer.Ordinal) && !element.TryGetProperty(name, out _)))
            throw new InvalidDataException($"Incomplete or unexpected {typeof(T).Name} properties.");
    }

    private void ValidateDocument(RollDocument roll)
    {
        if (roll.SchemaVersion != 1 || roll.Id == Guid.Empty || string.IsNullOrWhiteSpace(roll.Name)
            || roll.Captures is null || roll.Recipes is null || roll.Exports is null || roll.IntermediatesTrashed)
            throw new InvalidDataException("Unsupported or incomplete roll manifest.");
        CheckEnum(roll.FilmKind, nameof(roll.FilmKind));
        CheckEnum(roll.Phase, nameof(roll.Phase));
        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frameNumber = 0;
        foreach (var capture in roll.Captures)
        {
            if (capture is null || capture.Id == Guid.Empty || !ids.Add(capture.Id)
                || capture.IsDemo != roll.IsDemo || capture.CapturedAt == default)
                throw new InvalidDataException("Invalid capture identity or demo provenance.");
            CheckEnum(capture.Purpose, nameof(capture.Purpose));
            CheckEnum(capture.Mode, nameof(capture.Mode));
            ValidateChannels(capture.Mode, capture.Files);
            if (capture.Number != (capture.Purpose == CapturePurpose.Frame ? ++frameNumber : 0))
                throw new InvalidDataException("Frame numbering is not contiguous or a reference has a frame number.");
            foreach (var file in capture.Files)
            {
                var path = OwnedPath(file.Path, Path.Combine("captures", capture.Id.ToString("N")));
                if (!paths.Add(path))
                    throw new InvalidDataException("Capture files must be distinct.");
                RequireFile(path);
            }
        }
        var revisions = new Dictionary<Guid, int>();
        foreach (var recipe in roll.Recipes)
        {
            if (recipe is null)
                throw new InvalidDataException("Null recipe.");
            ValidateRecipeValues(recipe);
            if (recipe.FrameId is { } frameId && !roll.Captures.Any(c => c.Id == frameId && c.Purpose == CapturePurpose.Frame))
                throw new InvalidDataException("A frame recipe must identify a captured picture in this roll.");
            recipe.PictureCalibration?.Validate(roll, frameId: recipe.FrameId);
            if (recipe.FilmKind != roll.FilmKind
                || recipe.Revision != checked(revisions.GetValueOrDefault(recipe.Id) + 1)
                || recipe.Accepted != recipe.AcceptedAt.HasValue
                || recipe.AcceptedAt == default(DateTimeOffset))
                throw new InvalidDataException("Invalid recipe revision or acceptance.");
            revisions[recipe.Id] = recipe.Revision;
            if (recipe.XmpTemplatePath is not null)
                RequireFile(OwnedPath(recipe.XmpTemplatePath, "recipes"));
            if (recipe.Accepted)
                RequireCalibration(roll, recipe);
        }
        var latestAccepted = roll.Recipes.LastOrDefault(r => r.Accepted);
        if (roll.AcceptedRecipeId != latestAccepted?.Id)
            throw new InvalidDataException("Accepted recipe pointer does not match the saved acceptance.");
        ValidatePhase(roll, roll.Phase);
        foreach (var export in roll.Exports)
        {
            if (export is null)
                throw new InvalidDataException("Null export.");
            ValidateExport(roll, export, requireOutputs: false);
        }
    }

    private void ValidateExport(RollDocument roll, FrameExport export, bool requireOutputs)
    {
        CheckEnum(export.State, nameof(export.State));
        if (!roll.Captures.Any(c => c.Id == export.FrameId && c.Purpose == CapturePurpose.Frame))
            throw new InvalidDataException("Export must identify a captured frame.");
        if (!roll.Recipes.Any(r => r.Id == export.RecipeId && r.Revision == export.RecipeRevision && r.Accepted
            && (!r.FrameId.HasValue || r.FrameId == export.FrameId)))
            throw new InvalidDataException("Export must identify an accepted recipe revision.");
        if (export.UpdatedAt == default || export.OutputFiles is null)
            throw new InvalidDataException("Export timestamp and output list are required.");
        if (export.State is ExportState.Failed or ExportState.Canceled && string.IsNullOrWhiteSpace(export.Error))
            throw new InvalidDataException("Failed and canceled exports require an explicit error.");
        if (export.State == ExportState.Complete
            && (export.OutputFiles.Count == 0 || !string.IsNullOrEmpty(export.Error)))
            throw new InvalidDataException("Complete exports require output files and no error.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in export.OutputFiles)
        {
            var path = LocalPath(output);
            if (!paths.Add(path) || IsWithin(path, Path.Combine(RootPath, "captures"))
                || string.Equals(path, Path.Combine(RootPath, "captures"), StringComparison.OrdinalIgnoreCase)
                || IsWithin(path, Path.Combine(RootPath, "recipes"))
                || string.Equals(path, Path.Combine(RootPath, "recipes"), StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, RootPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetDirectoryName(path), RootPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Export outputs must be separate from captures, recipes, and roll metadata.");
            if (requireOutputs && export.State == ExportState.Complete)
                RequireFile(path);
        }
    }

    private static void ValidatePhase(RollDocument roll, WorkflowPhase phase)
    {
        if (phase == WorkflowPhase.Develop && !roll.Captures.Any(c => c.Purpose == CapturePurpose.Frame))
            throw new InvalidOperationException("Develop requires captured frames.");
        if (phase == WorkflowPhase.Export)
        {
            var accepted = roll.Recipes.LastOrDefault(r => r.Accepted && r.Id == roll.AcceptedRecipeId);
            if (accepted is null)
                throw new InvalidOperationException("Export requires an accepted recipe.");
            RequireCalibration(roll, accepted);
        }
    }

    private static void RequireCalibration(RollDocument roll, DevelopRecipe recipe)
    {
        if (recipe.FrameId is { } frameId)
        {
            var frame = roll.Captures.SingleOrDefault(c => c.Id == frameId && c.Purpose == CapturePurpose.Frame)
                ?? throw new InvalidOperationException("The recipe must belong to a captured frame.");
            if (recipe.PictureCalibration is not { } local)
                throw new InvalidOperationException("Prepare this frame's automatic calibration before accepting it.");
            local.Validate(roll, frame.Mode, frame.Id);
            return;
        }
        if (recipe.PictureCalibration is not { } picture)
        {
            RequireReferences(roll);
            return;
        }
        picture.Validate(roll);
        picture.RequireComplete(recipe.FilmKind);
        var frames = roll.Captures.Where(c => c.Purpose == CapturePurpose.Frame).ToArray();
        if (frames.Length == 0)
            throw new InvalidOperationException("Applying a recipe requires a captured picture.");
        foreach (var frame in frames) picture.Validate(roll, frame.Mode);
    }

    private static void RequireReferences(RollDocument roll)
    {
        var modes = roll.Captures.Where(c => c.Purpose == CapturePurpose.Frame).Select(c => c.Mode).Distinct().ToArray();
        if (modes.Length == 0)
            throw new InvalidOperationException("Accepting a recipe requires captured frames.");
        foreach (var mode in modes)
        {
            if (!roll.Captures.Any(c => c.Purpose == CapturePurpose.LightReference && c.Mode == mode))
                throw new InvalidOperationException($"A {mode} light reference is required.");
            if (roll.FilmKind != FilmKind.Positive
                && !roll.Captures.Any(c => c.Purpose == CapturePurpose.FilmBaseReference && c.Mode == mode))
                throw new InvalidOperationException($"A {mode} clear-film/base reference is required for negative film.");
        }
    }

    private static void ValidateRecipeValues(DevelopRecipe recipe)
    {
        recipe.PictureCalibration?.Light?.Validate(allowClipped: true);
        recipe.PictureCalibration?.FilmBase?.Validate();
        CheckEnum(recipe.FilmKind, nameof(recipe.FilmKind));
        double[] positive = [recipe.RedGain, recipe.GreenGain, recipe.BlueGain,
            recipe.FilmBaseRed, recipe.FilmBaseGreen, recipe.FilmBaseBlue];
        double[] margins = [recipe.CropLeft, recipe.CropTop, recipe.CropRight, recipe.CropBottom];
        if (recipe.Id == Guid.Empty || recipe.FrameId == Guid.Empty || !double.IsFinite(recipe.ExposureEv)
            || positive.Any(value => !double.IsFinite(value) || value <= 0)
            || margins.Any(value => !double.IsFinite(value) || value < 0 || value >= 1)
            || recipe.CropLeft + recipe.CropRight >= 1 || recipe.CropTop + recipe.CropBottom >= 1
            || !double.IsFinite(recipe.BlackPoint) || !double.IsFinite(recipe.WhitePoint)
            || recipe.BlackPoint < 0 || recipe.WhitePoint > 1 || recipe.BlackPoint >= recipe.WhitePoint)
            throw new ArgumentException("Recipe requires finite exposure, positive gains/base values, valid levels and nonempty crop extents.");
    }

    private static void ValidateChannels(CaptureMode mode, IReadOnlyList<CaptureFile>? files)
    {
        LightChannel[] expected = mode == CaptureMode.White
            ? [LightChannel.White] : [LightChannel.Red, LightChannel.Green, LightChannel.Blue];
        if (files is null || files.Count < expected.Length || files.Count > expected.Length * 2
            || files.Any(file => file is null)
            || files.Any(file => !expected.Contains(file.Channel)))
            throw new ArgumentException($"{mode} capture requires {string.Join(", ", expected)}, each as one image or a RAW+JPEG pair.");
        foreach (var channel in expected)
        {
            var group = files.Where(file => file.Channel == channel).ToArray();
            if (group.Length == 1) continue;
            if (group.Length != 2 || group.Count(file => Path.GetExtension(file.Path).Equals(".raf", StringComparison.OrdinalIgnoreCase)) != 1
                || group.Count(file => Path.GetExtension(file.Path).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(file.Path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) != 1)
                throw new ArgumentException($"{channel} requires one image or one Fujifilm RAW+JPEG pair.");
        }
    }

    private string CopyOwned(string source, string directory, string name)
    {
        source = LocalPath(source);
        RequireFile(source);
        directory = LocalPath(directory);
        Directory.CreateDirectory(directory);
        var target = LocalPath(Path.Combine(directory, name));
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.WriteThrough))
        {
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        RequireFile(target);
        return target;
    }

    private string OwnedPath(string path, string directory)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Owned paths must be absolute local paths.");
        path = LocalPath(path);
        if (!IsWithin(path, Path.Combine(RootPath, directory)))
            throw new InvalidDataException("Capture or recipe path escapes its roll-owned directory.");
        return path;
    }

    private static bool IsWithin(string path, string directory) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static string LocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0)
            throw new ArgumentException("Only local filesystem paths without alternate streams are supported.", nameof(path));
        for (string? part = full; part is not null; part = Path.GetDirectoryName(part))
        {
            if ((File.Exists(part) || Directory.Exists(part))
                && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Roll paths must not traverse symbolic links or reparse points.");
        }
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidDataException($"Required file is missing or empty: {path}");
    }

    private static void CheckEnum<T>(T value, string name) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(name, value, "Unknown workflow value.");
    }

    private static RollDocument Snapshot(RollDocument roll) => roll with
    {
        Captures = roll.Captures.Select(c => c with { Files = c.Files.ToArray() }).ToList(),
        Recipes = roll.Recipes.ToList(),
        Exports = roll.Exports.Select(e => e with { OutputFiles = e.OutputFiles.ToArray() }).ToList()
    };
}
