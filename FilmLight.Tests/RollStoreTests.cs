using System.Text.Json.Nodes;
using FilmLight.Core;
using FilmLight.Workflow;
using Xunit;

namespace FilmLight.Tests;

public sealed class RollStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "FilmLight-RollStoreTests-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(directory, "roll");

    [Fact]
    public void CaptureSetsRejectIncompleteDuplicateUnexpectedAndMissingFilesWithoutRecording()
    {
        var store = Create();
        var rgb = Files(CaptureMode.Rgb);
        Assert.Throws<ArgumentException>(() => store.AddCapture(CapturePurpose.Frame, CaptureMode.Rgb, rgb[..2], true));
        Assert.Throws<ArgumentException>(() => store.AddCapture(CapturePurpose.Frame, CaptureMode.Rgb,
            [rgb[0], rgb[0], rgb[2]], true));
        Assert.Throws<ArgumentException>(() => store.AddCapture(CapturePurpose.Frame, CaptureMode.White, [rgb[0]], true));
        Assert.Throws<ArgumentException>(() => store.AddCapture(CapturePurpose.Frame, CaptureMode.Rgb,
            [.. rgb, new(LightChannel.White, rgb[0].Path)], true));
        Assert.Throws<InvalidDataException>(() => store.AddCapture(CapturePurpose.Frame, CaptureMode.Rgb,
            [rgb[0], rgb[1], new(LightChannel.Blue, Path.Combine(directory, "missing.raw"))], true));
        var empty = WriteFile("empty.raw", "");
        Assert.Throws<InvalidDataException>(() => store.AddCapture(CapturePurpose.Frame, CaptureMode.White,
            [new(LightChannel.White, empty)], true));
        using (var locked = new FileStream(rgb[1].Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => store.AddCapture(CapturePurpose.Frame, CaptureMode.Rgb, rgb, true));
        }
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(Root, "captures"), "*", SearchOption.AllDirectories));
        Assert.Empty(RollStore.Open(Root).Document.Captures);
        Assert.Equal(1, Capture(store).Number);
    }

    [Fact]
    public void RawJpegPairsStayTogetherInOneFrameAndRejectDuplicateFormats()
    {
        var store = Create(isDemo: false);
        var files = new List<CaptureFile>();
        foreach (var channel in new[] { LightChannel.Red, LightChannel.Green, LightChannel.Blue })
        {
            files.Add(new(channel, WriteFile($"{channel}.RAF", "raw")));
            files.Add(new(channel, WriteFile($"{channel}.JPG", "jpeg")));
        }
        var capture = store.AddCapture(CapturePurpose.Frame, CaptureMode.Rgb, files, false);
        Assert.Equal(1, capture.Number);
        Assert.Equal(6, capture.Files.Count);
        Assert.Equal(6, RollStore.Open(Root).Document.Captures.Single().Files.Count);
        files[1] = new(LightChannel.Red, WriteFile("duplicate.RAF", "other raw"));
        Assert.Throws<ArgumentException>(() => store.AddCapture(CapturePurpose.Frame, CaptureMode.Rgb, files, false));
        Assert.Single(RollStore.Open(Root).Document.Captures);
    }

    [Fact]
    public void IngestOwnsFilesAndReopenPreservesNumbersAndDetachedSnapshots()
    {
        var store = Create();
        var source = Files(CaptureMode.Rgb);
        var first = store.AddCapture(CapturePurpose.Frame, CaptureMode.Rgb, source, true);
        Assert.Equal(1, first.Number);
        Assert.All(source, file => Assert.True(File.Exists(file.Path)));
        Assert.All(first.Files, file => Assert.StartsWith(Path.Combine(Root, "captures"), file.Path));
        Assert.Equal(0, Capture(store, CapturePurpose.LightReference).Number);
        Assert.Equal(0, Capture(store, CapturePurpose.LightReference).Number);
        var reopened = RollStore.Open(Root);
        Assert.Equal(2, Capture(reopened).Number);
        // A second instance must reload under the writer lease, not overwrite a newer manifest.
        Assert.Equal(3, Capture(store).Number);
        var snapshot = store.Document;
        var originalPath = first.Files[0].Path;
        ((CaptureFile[])first.Files)[0] = new(LightChannel.White, "caller edit");
        ((CaptureFile[])snapshot.Captures[0].Files)[0] = new(LightChannel.White, "caller edit");
        snapshot.Captures.Clear();
        Assert.Equal(originalPath, store.Document.Captures[0].Files[0].Path);
        Assert.Equal(5, RollStore.Open(Root).Document.Captures.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RealAndDemoCannotBeMixed(bool demo)
    {
        var store = Create(isDemo: demo);
        Assert.Throws<InvalidOperationException>(() => store.AddCapture(CapturePurpose.Frame,
            CaptureMode.White, Files(CaptureMode.White), !demo));
        Assert.Empty(RollStore.Open(Root).Document.Captures);
    }

    [Fact]
    public void AcceptanceRequiresFramesAndMatchingReferencesAndNegativeBase()
    {
        var store = Create(FilmKind.ColorNegative);
        var recipe = new DevelopRecipe { FilmKind = FilmKind.ColorNegative };
        Assert.Throws<InvalidOperationException>(() => store.SaveRecipe(recipe, true));
        Assert.Throws<InvalidOperationException>(() => store.SetPhase(WorkflowPhase.Develop));
        Assert.Throws<InvalidOperationException>(() => store.SetPhase(WorkflowPhase.Export));
        Capture(store, mode: CaptureMode.Rgb);
        Capture(store, CapturePurpose.LightReference);
        Assert.Throws<InvalidOperationException>(() => store.SaveRecipe(recipe, true));
        Capture(store, CapturePurpose.LightReference, CaptureMode.Rgb);
        Assert.Throws<InvalidOperationException>(() => store.SaveRecipe(recipe, true));
        Capture(store, CapturePurpose.FilmBaseReference, CaptureMode.Rgb);
        store.SetPhase(WorkflowPhase.Capture);
        store.SetPhase(WorkflowPhase.Develop);
        Assert.True(store.SaveRecipe(recipe, true).Accepted);
        store.SetPhase(WorkflowPhase.Export);
        Assert.Equal(WorkflowPhase.Export, RollStore.Open(Root).Document.Phase);
    }

    [Fact]
    public void AcceptedRevisionsAndXmpRemainImmutableWhenLaterDraftsAreSaved()
    {
        var store = Ready();
        var xmp = WriteFile("external.xmp", "accepted template");
        var accepted = store.SaveRecipe(new DevelopRecipe { FilmKind = FilmKind.Positive, XmpTemplatePath = xmp }, true);
        File.WriteAllText(xmp, "edited template");
        var draft = store.SaveRecipe(accepted with { ExposureEv = 2, XmpTemplatePath = xmp }, false);
        Assert.Equal(2, draft.Revision);
        Assert.False(draft.Accepted);
        Assert.Null(draft.AcceptedAt);
        Assert.Equal("accepted template", File.ReadAllText(accepted.XmpTemplatePath!));
        var next = store.SaveRecipe(draft, true);
        Assert.Equal(3, next.Revision);
        var reopened = RollStore.Open(Root);
        Assert.Equal([1, 2, 3], reopened.Document.Recipes.Select(r => r.Revision));
        Assert.True(reopened.Document.Recipes[0].Accepted);
        Assert.Equal(0, reopened.Document.Recipes[0].ExposureEv);
        Assert.Equal(accepted.Id, reopened.Document.AcceptedRecipeId);
        Assert.NotEqual(accepted.XmpTemplatePath, next.XmpTemplatePath);
    }

    [Fact]
    public void InvalidRecipeValuesAreRejectedWithoutChangingTheManifest()
    {
        var store = Ready();
        var recipe = new DevelopRecipe { FilmKind = FilmKind.Positive };
        DevelopRecipe[] invalid =
        [
            recipe with { ExposureEv = double.NaN },
            recipe with { ExposureEv = double.PositiveInfinity },
            recipe with { RedGain = 0 },
            recipe with { GreenGain = -1 },
            recipe with { BlueGain = double.PositiveInfinity },
            recipe with { FilmBaseRed = 0 },
            recipe with { FilmBaseGreen = double.NaN },
            recipe with { CropLeft = .6, CropRight = .4 },
            recipe with { CropTop = .5, CropBottom = .6 },
            recipe with { CropBottom = -.1 },
            recipe with { CropRight = double.NaN },
            recipe with { BlackPoint = -.1 },
            recipe with { WhitePoint = 1.1 },
            recipe with { BlackPoint = .5, WhitePoint = .5 },
            recipe with { WhitePoint = double.NaN },
            recipe with { FilmKind = FilmKind.BlackAndWhiteNegative }
        ];
        var before = File.ReadAllText(Path.Combine(Root, "roll.json"));
        foreach (var draft in invalid)
            Assert.Throws<ArgumentException>(() => store.SaveRecipe(draft, true));
        Assert.Equal(before, File.ReadAllText(Path.Combine(Root, "roll.json")));
        Assert.Empty(store.Document.Recipes);
    }

    [Fact]
    public void ExportRequiresFrameAcceptedRevisionAndRealOutputsAndRetainsFailures()
    {
        var store = Ready();
        var frame = store.Document.Captures.Single(c => c.Purpose == CapturePurpose.Frame);
        var reference = store.Document.Captures.Single(c => c.Purpose == CapturePurpose.LightReference);
        var draft = store.SaveRecipe(new DevelopRecipe { FilmKind = FilmKind.Positive }, false);
        var output = WriteFile("exports\\frame.tif", "export data");
        var complete = new FrameExport(frame.Id, draft.Id, draft.Revision, ExportState.Complete,
            [output], null, DateTimeOffset.UtcNow);
        Assert.Throws<InvalidDataException>(() => store.RecordExport(complete));
        var accepted = store.SaveRecipe(draft, true);
        complete = complete with { RecipeRevision = accepted.Revision };
        Assert.Throws<InvalidDataException>(() => store.RecordExport(complete with { FrameId = reference.Id }));
        Assert.Throws<InvalidDataException>(() => store.RecordExport(complete with { FrameId = Guid.NewGuid() }));
        Assert.Throws<InvalidDataException>(() => store.RecordExport(complete with { RecipeRevision = 999 }));
        Assert.Throws<InvalidDataException>(() => store.RecordExport(complete with { OutputFiles = [] }));
        Assert.Throws<InvalidDataException>(() => store.RecordExport(complete with { OutputFiles = [Path.Combine(directory, "missing.tif")] }));
        Assert.Throws<InvalidDataException>(() => store.RecordExport(complete with { OutputFiles = [WriteFile("zero.tif", "")] }));
        Assert.Throws<InvalidDataException>(() => store.RecordExport(complete with { OutputFiles = [frame.Files[0].Path] }));
        Assert.Throws<InvalidDataException>(() => store.RecordExport(complete with { State = ExportState.Failed }));
        store.RecordExport(complete with { State = ExportState.Failed, OutputFiles = [], Error = "processor failed" });
        store.RecordExport(complete with { State = ExportState.Canceled, OutputFiles = [], Error = "user canceled" });
        var outputs = new List<string> { output };
        store.RecordExport(complete with { OutputFiles = outputs });
        outputs.Clear();
        var snapshot = store.Document;
        ((string[])snapshot.Exports[2].OutputFiles)[0] = "caller edit";
        snapshot.Exports.Clear();
        var reopened = RollStore.Open(Root).Document;
        Assert.Equal(3, reopened.Exports.Count);
        Assert.Equal("processor failed", reopened.Exports[0].Error);
        Assert.Equal("user canceled", reopened.Exports[1].Error);
        Assert.Equal(output, Assert.Single(reopened.Exports[2].OutputFiles));
        Assert.Equal(2, reopened.Captures.Count);
        File.WriteAllText(output, "");
        var retry = RollStore.Open(Root);
        Assert.Throws<InvalidDataException>(() => retry.RecordExport(complete));
        retry.RecordExport(complete with { State = ExportState.Failed, OutputFiles = [], Error = "Output was removed or corrupted; retry required." });
        Assert.Equal(ExportState.Failed, RollStore.Open(Root).Document.Exports.Last().State);
    }

    [Fact]
    public void OpenRejectsCorruptSchemaSetsPathsIdentityAndMissingOwnedFiles()
    {
        var store = Ready();
        store.SaveRecipe(new DevelopRecipe { FilmKind = FilmKind.Positive }, true);
        var manifest = Path.Combine(Root, "roll.json");
        var good = File.ReadAllText(manifest);
        Action<JsonObject>[] corruptions =
        [
            json => json["SchemaVersion"] = 2,
            json => json.Remove("IsDemo"),
            json => json["Captures"]![0]!["Files"] = new JsonArray(),
            json => json["Captures"]![0]!["Files"]![0]!["Path"] = WriteFile("outside.raw", "not owned"),
            json => json["Captures"]![0]!["IsDemo"] = false,
            json => json["Captures"]![0]!["Number"] = 42,
            json => json["Captures"]![0]!["Mode"] = 999,
            json => json["Recipes"]![0]!["Revision"] = 4,
            json => json["Recipes"]![0]!["AcceptedAt"] = null,
            json => json["AcceptedRecipeId"] = Guid.NewGuid().ToString(),
            json => json["Exports"] = null
        ];
        foreach (var corrupt in corruptions)
        {
            var json = JsonNode.Parse(good)!.AsObject();
            corrupt(json);
            File.WriteAllText(manifest, json.ToJsonString());
            Assert.Throws<InvalidDataException>(() => RollStore.Open(Root));
        }
        File.WriteAllText(manifest, "{\"SchemaVersion\":");
        Assert.Throws<InvalidDataException>(() => RollStore.Open(Root));
        File.WriteAllText(manifest, good);
        File.WriteAllText(store.Document.Captures[0].Files[0].Path, "");
        Assert.Throws<InvalidDataException>(() => RollStore.Open(Root));
    }

    private RollStore Create(FilmKind kind = FilmKind.Positive, bool isDemo = true) =>
        RollStore.Create(Root, "Test roll", kind, isDemo);

    private RollStore Ready()
    {
        var store = Create();
        Capture(store);
        Capture(store, CapturePurpose.LightReference);
        return store;
    }

    private RollCapture Capture(RollStore store, CapturePurpose purpose = CapturePurpose.Frame,
        CaptureMode mode = CaptureMode.White) => store.AddCapture(purpose, mode, Files(mode), store.Document.IsDemo);

    private CaptureFile[] Files(CaptureMode mode)
    {
        LightChannel[] channels = mode == CaptureMode.White
            ? [LightChannel.White] : [LightChannel.Red, LightChannel.Green, LightChannel.Blue];
        return channels.Select(channel => new CaptureFile(channel,
            WriteFile(Guid.NewGuid().ToString("N") + ".raw", channel.ToString()))).ToArray();
    }

    private string WriteFile(string name, string contents)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
