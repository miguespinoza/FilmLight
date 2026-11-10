using System.Security.Cryptography;
using System.Runtime.Versioning;
using FilmLight.Core;
using FilmLight.Workflow;
using Xunit;

namespace FilmLight.Tests;

[SupportedOSPlatform("windows")]
public sealed class WorkflowIntegrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "FilmLight-Workflow-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(CaptureMode.White, FilmKind.ColorNegative)]
    [InlineData(CaptureMode.Rgb, FilmKind.ColorNegative)]
    [InlineData(CaptureMode.White, FilmKind.BlackAndWhiteNegative)]
    [InlineData(CaptureMode.White, FilmKind.Positive)]
    public async Task SimulatedRollCanBeCapturedReviewedExportedAndReopenedWithoutChangingInputs(CaptureMode mode, FilmKind filmKind)
    {
        var store = RollStore.Create(Path.Combine(directory, "roll"), "Synthetic acceptance roll", filmKind, true);
        IWorkflowCaptureSource source = new DemoCaptureSource();
        var shown = new List<LightChannel>();
        store.SetPhase(WorkflowPhase.Capture);
        await Capture(CapturePurpose.Frame);
        await Capture(CapturePurpose.Frame);

        LightChannel[] channels = mode == CaptureMode.White
            ? [LightChannel.White] : [LightChannel.Red, LightChannel.Green, LightChannel.Blue];
        Assert.Equal(Enumerable.Range(0, 2).SelectMany(_ => channels), shown);
        Assert.All(store.Document.Captures, c => Assert.Equal(CapturePurpose.Frame, c.Purpose));
        Assert.Equal([1, 2], store.Document.Captures.Where(c => c.Purpose == CapturePurpose.Frame).Select(c => c.Number));
        Assert.False(Directory.Exists(Path.Combine(store.RootPath, "exports")));
        var originals = store.Document.Captures.SelectMany(c => c.Files).ToDictionary(f => f.Path, f => Hash(f.Path));

        store.SetPhase(WorkflowPhase.Develop);
        var representative = store.Document.Captures.First(c => c.Purpose == CapturePurpose.Frame);
        using var workflow = new FrameWorkflow(store, null);
        var preview = await workflow.PreviewAsync(representative.Id, null, default);
        Assert.True(File.Exists(preview.PositivePath));
        Assert.True(new FileInfo(preview.PositivePath).Length > 100);
        Assert.NotNull(preview.SourcePath);
        preview = await workflow.PickAsync(representative.Id, CalibrationTarget.Backlight,
            3.0 / 384, .5, null, default);
        if (filmKind != FilmKind.Positive)
        {
            preview = await workflow.PickAsync(representative.Id, CalibrationTarget.FilmBase,
                12.0 / 384, .5, null, default);
        }
        Assert.Empty(store.Document.Exports);
        var accepted = workflow.AcceptedRecipe(representative.Id)!;
        store.SetPhase(WorkflowPhase.Export);

        var summary = await workflow.ExportAsync(store.Document.Captures.Select(c => c.Id).ToArray(), null, default);
        Assert.Equal(2, summary.Completed);
        Assert.Equal(0, summary.Failed);
        foreach (var frame in store.Document.Captures.Where(c => c.Purpose == CapturePurpose.Frame))
        {
            var local = workflow.AcceptedRecipe(frame.Id)!;
            Assert.Equal(frame.Id, local.FrameId);
            Assert.Equal(frame.Id, local.PictureCalibration!.Light!.FrameId);
            var result = store.Document.Exports.Last(e => e.FrameId == frame.Id);
            Assert.Contains(result.OutputFiles, p => Path.GetExtension(p).Equals(".tif", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(p).Equals(".tiff", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.OutputFiles, p => Path.GetExtension(p).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(p).Equals(".jpeg", StringComparison.OrdinalIgnoreCase));
            Assert.All(result.OutputFiles, p => Assert.True(new FileInfo(p).Length > 100));
        }

        var reopened = RollStore.Open(store.RootPath);
        Assert.Equal(accepted, reopened.Document.Recipes.Last(r => r.FrameId == representative.Id && r.Accepted));
        Assert.Equal(2, reopened.Document.Recipes.Select(r => r.FrameId).Distinct().Count());
        Assert.Equal(WorkflowPhase.Export, reopened.Document.Phase);
        Assert.Equal(2, reopened.Document.Exports.Count(e => e.State == ExportState.Complete));
        Assert.All(originals, pair => Assert.Equal(pair.Value, Hash(pair.Key)));

        using var resumed = new FrameWorkflow(reopened, null);
        var edited = resumed.Edit(representative.Id, resumed.Draft(representative.Id) with { ExposureEv = 1 });
        var changed = await resumed.PreviewAsync(representative.Id, null, default);
        Assert.NotEqual(Hash(preview.PositivePath), Hash(changed.PositivePath));
        var revision = resumed.Accept(changed);
        Assert.Equal(edited.Revision + 1, revision.Revision);
        Assert.All(reopened.Document.Exports, e => Assert.Contains(reopened.Document.Recipes,
            recipe => recipe.FrameId == e.FrameId && recipe.Id == e.RecipeId && recipe.Revision == e.RecipeRevision));

        async Task Capture(CapturePurpose purpose)
        {
            var number = store.Document.Captures.Count(c => c.Purpose == CapturePurpose.Frame) + 1;
            var files = await source.CaptureAsync(purpose, mode, number,
                Path.Combine(directory, "incoming", Guid.NewGuid().ToString("N")),
                (channel, _) => { shown.Add(channel); return Task.CompletedTask; }, CancellationToken.None);
            store.AddCapture(purpose, mode, files, source.IsDemo);
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task PreviewSupportsRollFoldersBeyondTheLegacyWindowsPathLimit()
    {
        var store = RollStore.Create(Path.Combine(directory, "roll"), "Long-path roll", FilmKind.ColorNegative, true);
        var source = new DemoCaptureSource();
        foreach (var purpose in new[] { CapturePurpose.LightReference, CapturePurpose.FilmBaseReference, CapturePurpose.Frame })
        {
            var files = await source.CaptureAsync(purpose, CaptureMode.White, 1,
                Path.Combine(directory, "incoming", Guid.NewGuid().ToString("N")),
                (_, _) => Task.CompletedTask, CancellationToken.None);
            store.AddCapture(purpose, CaptureMode.White, files, true);
        }
        var output = Path.Combine(directory, new string('a', 90), new string('b', 90), "previews");
        var result = await new DemoFrameProcessor().PreviewAsync(store.Document,
            store.Document.Captures.Single(c => c.Purpose == CapturePurpose.Frame),
            new DevelopRecipe { FilmKind = FilmKind.ColorNegative }, output, null, CancellationToken.None);
        Assert.True(result.ImagePath.Length > 260);
        Assert.True(new FileInfo(result.ImagePath).Length > 100);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
