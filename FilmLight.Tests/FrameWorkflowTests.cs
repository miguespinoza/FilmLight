using System.Runtime.Versioning;
using FilmLight.Workflow;
using Xunit;

namespace FilmLight.Tests;

[SupportedOSPlatform("windows")]
public sealed class FrameWorkflowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "FilmLight-frame-workflow-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FramesStartFromDefaultsRatherThanRollWideSettings()
    {
        var store = RollStore.Create(Path.Combine(directory, "roll"), "Defaults", FilmKind.ColorNegative, true);
        using (var workflow = new FrameWorkflow(store, null))
            workflow.Edit(null, workflow.Draft(null) with { ExposureEv = .5 });
        store = RollStore.Open(store.RootPath);
        using var resumed = new FrameWorkflow(store, null);
        Assert.Equal(.5, resumed.Draft(null).ExposureEv);
        var files = await new DemoCaptureSource().CaptureAsync(CapturePurpose.Frame, CaptureMode.White, 1,
            Path.Combine(directory, "incoming"), (_, _) => Task.CompletedTask, default);
        var frame = store.AddCapture(CapturePurpose.Frame, CaptureMode.White, files, true);
        var preview = await resumed.PreviewAsync(frame.Id, null, default);
        Assert.Equal(0, preview.Recipe.ExposureEv);
        Assert.Equal(frame.Id, preview.Recipe.FrameId);
        Assert.True(preview.PicksReady);
    }

    [Fact]
    public async Task DraftsRestorePerFrameWithoutSharingEditsOrPicks()
    {
        var (store, frames) = await Roll(2);
        DevelopRecipe first, second;
        using (var workflow = new FrameWorkflow(store, null))
        {
            var preview = await workflow.PreviewAsync(frames[0].Id, null, default);
            first = workflow.Edit(frames[0].Id, preview.Recipe with { ExposureEv = .75 });
            var next = workflow.Draft(frames[1].Id);
            Assert.Equal(.75, next.ExposureEv);
            Assert.Null(next.PictureCalibration);
            Assert.NotEqual(first.Id, next.Id);
            second = workflow.Edit(frames[1].Id, next with { ExposureEv = -.5 });
            Assert.Equal(first, workflow.Draft(frames[0].Id));
            Assert.Equal(second, workflow.Draft(frames[1].Id));
        }
        using var resumed = new FrameWorkflow(RollStore.Open(store.RootPath), null);
        Assert.Equal(first, resumed.Draft(frames[0].Id));
        Assert.Equal(second, resumed.Draft(frames[1].Id));
        Assert.Equal(frames[0].Id, first.PictureCalibration!.Light!.FrameId);
        Assert.Null(second.PictureCalibration);
    }

    [Fact]
    public async Task PreviewKeepsItsExactSnapshotWhenSettingsChange()
    {
        var (store, frames) = await Roll();
        using var workflow = new FrameWorkflow(store, null);
        var preview = await workflow.PreviewAsync(frames[0].Id, null, default);
        Assert.Equal(workflow.Draft(frames[0].Id), preview.Recipe);
        Assert.True(preview.PicksReady);
        Assert.True(File.Exists(preview.PositivePath));
        Assert.True(File.Exists(preview.SourcePath));
        Assert.Contains(preview.Warnings, w => w.Contains("SYNTHETIC"));
        var saved = workflow.Edit(frames[0].Id, preview.Recipe with { ExposureEv = 1 });
        Assert.Equal(0, preview.Recipe.ExposureEv);
        Assert.Equal(1, saved.ExposureEv);
        Assert.Equal(saved, store.Document.Recipes.Last());
        Assert.Equal(saved, workflow.Draft(frames[0].Id));
    }

    [Fact]
    public async Task XmpChangedDuringRenderingDoesNotIssueAPreview()
    {
        var (store, frames) = await Roll();
        using var workflow = new FrameWorkflow(store, null);
        var xmp = Path.Combine(directory, "import.xmp");
        File.WriteAllText(xmp, "<xmp>first</xmp>");
        workflow.Edit(frames[0].Id, workflow.Draft(frames[0].Id) with { XmpTemplatePath = xmp });
        var progress = new Callback<ProcessingProgress>(p =>
        {
            if (p.Stage == "Publishing output")
                File.WriteAllText(workflow.Draft(frames[0].Id).XmpTemplatePath!, "<xmp>changed</xmp>");
        });
        Assert.Equal("The imported XMP changed during preview. Import a stable calibrated XMP and render again.",
            (await Assert.ThrowsAsync<InvalidDataException>(() =>
                workflow.PreviewAsync(frames[0].Id, progress, default))).Message);
    }

    [Fact]
    public async Task ExportUsesEachFramesLatestOverrideWithoutPreview()
    {
        var (store, frames) = await Roll(2);
        using var workflow = new FrameWorkflow(store, null);
        workflow.Edit(frames[0].Id, workflow.Draft(frames[0].Id) with { ExposureEv = .75, RedGain = 1.1 });
        var first = workflow.Draft(frames[0].Id);
        workflow.Edit(frames[1].Id, workflow.Draft(frames[1].Id) with { ExposureEv = -2 });
        var updates = new List<ExportProgress>();
        var summary = await workflow.ExportAsync([frames[1].Id], null, default, new Callback<ExportProgress>(updates.Add));
        Assert.Equal(1, summary.Completed);
        Assert.Equal(0, summary.Failed);
        var local = workflow.Draft(frames[1].Id);
        Assert.Equal(-2, local.ExposureEv);
        Assert.Equal(1.1, local.RedGain);
        Assert.NotEqual(first.Id, local.Id);
        Assert.Equal(first, workflow.Draft(frames[0].Id));
        Assert.Equal(frames[1].Id, local.PictureCalibration!.Light!.FrameId);
        Assert.Equal(frames[1].Id, local.PictureCalibration.FilmBase!.FrameId);
        Assert.Equal(local.PictureCalibration, workflow.Draft(frames[1].Id).PictureCalibration);
        Assert.Equal("Batch finished: 1/1 complete, 0 failed. Each frame used its own latest saved calibration/settings. Inputs retained; retry missing/failed jobs explicitly.", summary.Message);
        Assert.Contains(updates, p => p.Context == $"Batch: 0/1 complete, 0 failed. Exporting frame 2 with saved revision {local.Revision}.");
        var complete = store.Document.Exports.Last();
        Assert.Equal(ExportState.Complete, complete.State);
        Assert.All(complete.OutputFiles, p =>
        {
            Assert.StartsWith(workflow.PositiveRoot, p);
            Assert.StartsWith("SYNTHETIC-Frame-0002", Path.GetFileName(p));
            Assert.True(new FileInfo(p).Length > 0);
        });
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(complete.OutputFiles[0])!, "recipe.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(store.RootPath, "Processing"), "positive-linear.tif", SearchOption.AllDirectories));
        Assert.All(frames.SelectMany(f => f.Files), f => Assert.True(File.Exists(f.Path)));
    }

    [Fact]
    public async Task ExportReusesCompleteOutputsAndRegeneratesMissingOnesInANewFolder()
    {
        var (store, frames) = await Roll();
        using var workflow = new FrameWorkflow(store, null);
        await workflow.PreviewAsync(frames[0].Id, null, default);
        await workflow.ExportAsync([frames[0].Id], null, default);
        var first = store.Document.Exports.Last();
        var records = store.Document.Exports.Count;
        var reused = await workflow.ExportAsync([frames[0].Id], null, default);
        Assert.Equal(1, reused.Completed);
        Assert.Equal(records, store.Document.Exports.Count);
        Assert.Equal(first.OutputFiles, store.Document.Exports.Last().OutputFiles);
        Assert.Equal(first.UpdatedAt, store.Document.Exports.Last().UpdatedAt);
        File.Delete(first.OutputFiles[0]);
        var regenerated = await workflow.ExportAsync([frames[0].Id], null, default);
        Assert.Equal(1, regenerated.Completed);
        Assert.Equal(0, regenerated.Failed);
        Assert.Equal([ExportState.Failed, ExportState.Processing, ExportState.Complete],
            store.Document.Exports.Skip(records).Select(e => e.State));
        Assert.Equal("Saved outputs are missing or empty; regenerating in a new folder.",
            store.Document.Exports[records].Error);
        var last = store.Document.Exports.Last();
        Assert.NotEqual(Path.GetDirectoryName(first.OutputFiles[0]), Path.GetDirectoryName(last.OutputFiles[0]));
        Assert.All(last.OutputFiles, p => Assert.True(new FileInfo(p).Length > 0));
    }

    [Fact]
    public async Task ExportAfterReopeningUsesLatestEditsRatherThanAnOlderExportedRevision()
    {
        var (store, frames) = await Roll(2);
        DevelopRecipe changed;
        using (var workflow = new FrameWorkflow(store, null))
        {
            workflow.Edit(frames[1].Id, workflow.Draft(frames[1].Id) with { ExposureEv = -.25 });
            var first = await workflow.ExportAsync(frames.Select(f => f.Id).ToArray(), null, default);
            Assert.Equal(2, first.Completed);
            changed = workflow.Edit(frames[0].Id, workflow.Draft(frames[0].Id) with
            {
                ExposureEv = 1.5, RedGain = 1.2
            });
        }
        var reopened = RollStore.Open(store.RootPath);
        var secondFrame = reopened.Document.Recipes.Last(r => r.FrameId == frames[1].Id);
        var earlier = reopened.Document.Exports.Last(e => e.FrameId == frames[0].Id);
        using var resumed = new FrameWorkflow(reopened, null);
        var result = await resumed.ExportAsync(frames.Select(f => f.Id).ToArray(), null, default);
        Assert.Equal(2, result.Completed);
        Assert.Equal(0, result.Failed);
        var exported = reopened.Document.Exports.Last(e => e.FrameId == frames[0].Id);
        Assert.Equal(changed.Revision, exported.RecipeRevision);
        Assert.NotEqual(earlier.OutputFiles, exported.OutputFiles);
        Assert.Equal(changed, resumed.Draft(frames[0].Id));
        Assert.Equal(secondFrame, resumed.Draft(frames[1].Id));
        Assert.Single(reopened.Document.Exports, e => e.FrameId == frames[1].Id && e.State == ExportState.Complete);
    }

    [Fact]
    public async Task UneditedFramesFollowPreviousSiblingUntilControlsAreOverriddenAndKeepTheirOwnPicks()
    {
        var (store, frames) = await Roll(3);
        var xmp = Path.Combine(directory, "inherited.xmp");
        File.WriteAllText(xmp, "<xmp>inherited controls</xmp>");
        using (var workflow = new FrameWorkflow(store, null))
        {
            workflow.Edit(frames[0].Id, workflow.Draft(frames[0].Id) with
            {
                ExposureEv = .5, RedGain = 1.1, XmpTemplatePath = xmp
            });
            // Resolve out of order: inheritance follows frame numbering, not the last selection.
            Assert.Equal(.5, workflow.Draft(frames[2].Id).ExposureEv);
            await workflow.PreviewAsync(frames[1].Id, null, default);
            Assert.True(workflow.Draft(frames[1].Id).InheritControls);
            Assert.Equal("<xmp>inherited controls</xmp>", File.ReadAllText(workflow.Draft(frames[1].Id).XmpTemplatePath!));
            var picks = workflow.Draft(frames[1].Id).PictureCalibration;
            Assert.Equal(frames[1].Id, picks!.Light!.FrameId);
            workflow.Edit(frames[0].Id, workflow.Draft(frames[0].Id) with { ExposureEv = 1 });
            var inherited = workflow.Draft(frames[1].Id);
            Assert.Equal(1, inherited.ExposureEv);
            Assert.Equal(1, workflow.Draft(frames[2].Id).ExposureEv);
            Assert.Equal(picks, inherited.PictureCalibration);
            workflow.Edit(frames[1].Id, inherited with { ExposureEv = -.5 });
            workflow.Edit(frames[0].Id, workflow.Draft(frames[0].Id) with { ExposureEv = 2 });
            Assert.Equal(-.5, workflow.Draft(frames[1].Id).ExposureEv);
            Assert.Equal(-.5, workflow.Draft(frames[2].Id).ExposureEv);
            Assert.Null(workflow.Draft(frames[2].Id).PictureCalibration);
            var result = await workflow.ExportAsync(frames.Select(f => f.Id).ToArray(), null, default);
            Assert.Equal(3, result.Completed);
        }
        var reopened = RollStore.Open(store.RootPath);
        using var resumed = new FrameWorkflow(reopened, null);
        Assert.False(resumed.Draft(frames[1].Id).InheritControls);
        Assert.True(resumed.Draft(frames[2].Id).InheritControls);
        resumed.Edit(frames[1].Id, resumed.Draft(frames[1].Id) with { BlueGain = 1.3 });
        Assert.Equal(1.3, resumed.Draft(frames[2].Id).BlueGain);
        var exported = await resumed.ExportAsync([frames[2].Id], null, default);
        Assert.Equal(1, exported.Completed);
        Assert.Equal(frames[2].Id, resumed.Draft(frames[2].Id).PictureCalibration!.Light!.FrameId);
        var records = reopened.Document.Exports.Count;
        await resumed.ExportAsync([frames[2].Id], null, default);
        Assert.Equal(records, reopened.Document.Exports.Count);
    }

    [Fact]
    public async Task WorkflowRetainsOneProcessorAndSourcesUntilDisposal()
    {
        var (store, frames) = await Roll(2);
        var created = 0;
        FrameSources? owned = null;
        var workflow = new FrameWorkflow(store, null, sources =>
        {
            created++;
            owned = sources;
            return new DemoFrameProcessor(sources);
        });
        try
        {
            await workflow.PreviewAsync(frames[0].Id, null, default);
            var firstSources = owned;
            Assert.Same(firstSources, workflow.Sources);
            workflow.Edit(frames[0].Id, workflow.Draft(frames[0].Id) with { ExposureEv = .5 });
            await workflow.PreviewAsync(frames[0].Id, null, default);
            Assert.Same(firstSources, workflow.Sources);
            await workflow.PreviewAsync(frames[1].Id, null, default);
            await workflow.ExportAsync([frames[0].Id], null, default);
            Assert.Equal(1, created);
            Assert.Same(firstSources, owned);
            Assert.Same(firstSources, workflow.Sources);
            _ = owned!.Open(store.Document, frames[0]);
        }
        finally { workflow.Dispose(); }
        Assert.Throws<ObjectDisposedException>(() => owned!.Open(store.Document, frames[0]));
        Assert.Throws<ObjectDisposedException>(() => workflow.Draft(frames[0].Id));
        workflow.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringRenderingOrPublishingNeverRecordsComplete(bool publishing)
    {
        var (store, frames) = await Roll();
        using var workflow = new FrameWorkflow(store, null);
        await workflow.PreviewAsync(frames[0].Id, null, default);
        using var cancel = new CancellationTokenSource();
        var processing = new Callback<ProcessingProgress>(p =>
        {
            if (!publishing && p.Stage == "Rendering synthetic positive") cancel.Cancel();
        });
        var exporting = new Callback<ExportProgress>(p =>
        {
            if (publishing && p.ActivityStage == "Publishing positives") cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            workflow.ExportAsync([frames[0].Id], processing, cancel.Token, exporting));
        Assert.Equal([ExportState.Processing, ExportState.Canceled], store.Document.Exports.Select(e => e.State));
        Assert.Equal("Canceled by operator; partial outputs and all inputs retained.", store.Document.Exports.Last().Error);
        Assert.DoesNotContain(RollStore.Open(store.RootPath).Document.Exports, e => e.State == ExportState.Complete);
        var retried = await workflow.ExportAsync([frames[0].Id], null, default);
        Assert.Equal(1, retried.Completed);
    }

    [Fact]
    public async Task AlreadyCanceledExportDoesNotStartAJob()
    {
        var (store, frames) = await Roll();
        using var workflow = new FrameWorkflow(store, null);
        await workflow.PreviewAsync(frames[0].Id, null, default);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            workflow.ExportAsync([frames[0].Id], null, cancel.Token));
        Assert.Empty(store.Document.Exports);
    }

    [Fact]
    public async Task InvalidExportRecordsFailureAndContinuesWithTheNextFrame()
    {
        var (store, frames) = await Roll(2);
        using var workflow = new FrameWorkflow(store, null, sources => new InvalidFirstExportProcessor(sources));
        await workflow.PreviewAsync(frames[0].Id, null, default);
        var summary = await workflow.ExportAsync(frames.Select(f => f.Id).ToArray(), null, default);
        Assert.Equal(1, summary.Completed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal([ExportState.Processing, ExportState.Failed, ExportState.Processing, ExportState.Complete],
            store.Document.Exports.Select(e => e.State));
        Assert.Equal("Processor did not produce both a nonempty TIFF and JPG.", store.Document.Exports[1].Error);
        Assert.Contains("Frame 1 FAILED: Processor did not produce both a nonempty TIFF and JPG.", summary.Warnings);
        Assert.StartsWith("Batch finished: 1/2 complete, 1 failed.", summary.Message);
    }

    [Fact]
    public async Task AutomaticAndManualPicksSaveOnlyToTheirOwnFrameAndKeepPreview()
    {
        var (store, frames) = await Roll(2);
        using var workflow = new FrameWorkflow(store, null);
        var automatic = await workflow.AutoPickAsync(frames[0].Id, null, default);
        Assert.True(automatic.PicksReady);
        Assert.Contains("Automatic picks from frame 1 (linear RGB):", automatic.SamplingMessage);
        Assert.Equal(automatic.Recipe, store.Document.Recipes.Last());
        var manual = await workflow.PickAsync(frames[0].Id, CalibrationTarget.Backlight, 3.0 / 384, .5, null, default);
        Assert.True(manual.PicksReady);
        Assert.Equal("Preview rendered for the exact current recipe. Light and film picks are applied to this frame only.", manual.Message);
        Assert.Equal(store.Document.Recipes.Last().PictureCalibration, manual.Recipe.PictureCalibration);
        Assert.Equal(workflow.Draft(frames[0].Id), manual.Recipe);
        Assert.DoesNotContain(store.Document.Recipes, r => r.FrameId == frames[1].Id);
        Assert.Null(workflow.Draft(frames[1].Id).PictureCalibration);
    }

    [Fact]
    public async Task FailedDetectionCanBePreviewedAndExportedWithProvisionalWarnings()
    {
        var (store, frames) = await Roll();
        var file = frames[0].Files[0].Path;
        ProcessingImages.WriteTiff(file, new(80, 60, Enumerable.Repeat(.25f, 80 * 60 * 3).ToArray()),
            false, ProcessingImages.SyntheticLabel + "; " + ProcessingImages.LinearLabel, default,
            ProcessingImages.LinearRec2020Profile());
        using var workflow = new FrameWorkflow(store, null);
        var preview = await workflow.PreviewAsync(frames[0].Id, null, default);
        Assert.False(preview.PicksReady);
        Assert.Contains(preview.Warnings, w => w.StartsWith("Automatic calibration failed for frame 1:"));
        Assert.Contains(preview.Warnings, w => w.StartsWith("Uncalibrated/provisional output"));
        Assert.Contains("This frame is uncalibrated.", preview.Message);
        var summary = await workflow.ExportAsync([frames[0].Id], null, default);
        Assert.Equal(1, summary.Completed);
        Assert.Contains(summary.Warnings, w => w.Contains("Uncalibrated/provisional output"));
    }

    [Fact]
    public async Task ClippedManualBacklightIsSavedAsMarkerOnlyAndPreviewIsKept()
    {
        var (store, frames) = await Roll();
        var file = frames[0].Files[0].Path;
        var image = ProcessingImages.ReadLinear(file, default, true);
        for (int y = 126; y <= 130; y++)
        for (int x = 1; x <= 5; x++)
            image.Pixels[(y * image.Width + x) * 3] = 1.05f;
        ProcessingImages.WriteTiff(file, image, true, ProcessingImages.SyntheticLabel + "; " + ProcessingImages.LinearLabel,
            default, ProcessingImages.LinearRec2020Profile());
        using var workflow = new FrameWorkflow(store, null);
        var incomplete = await workflow.PickAsync(frames[0].Id, CalibrationTarget.FilmBase, 12.0 / 384, .5, null, default);
        Assert.False(incomplete.PicksReady);
        Assert.Equal(incomplete.Recipe, store.Document.Recipes.Last());
        var preview = await workflow.PickAsync(frames[0].Id, CalibrationTarget.Backlight, 3.0 / 384, .5, null, default);
        Assert.True(preview.PicksReady);
        Assert.True(preview.Recipe.PictureCalibration!.Light!.IsClipped);
        Assert.Contains("Backlight is clipped: marker saved, WB correction skipped.", preview.SamplingMessage);
        Assert.Equal("Preview rendered for the exact current recipe. Picks saved for this frame only; clipped backlight is marker-only, with WB correction skipped.",
            preview.Message);
        Assert.Contains(PictureCalibration.ClippedBacklightWarning, preview.Warnings);
        Assert.Equal(store.Document.Recipes.Last().PictureCalibration, preview.Recipe.PictureCalibration);
    }

    [Fact]
    public async Task RealRollNeverFallsBackToDemoWhenDarktableIsUnavailable()
    {
        var store = RollStore.Create(Path.Combine(directory, "real"), "Real", FilmKind.ColorNegative, false);
        var input = Path.Combine(directory, "camera.RAF");
        File.WriteAllText(input, "Processor selection must reject the missing CLI before reading this input.");
        var frame = store.AddCapture(CapturePurpose.Frame, CaptureMode.White,
            [new(FilmLight.Core.LightChannel.White, input)], false);
        using var workflow = new FrameWorkflow(store, null);
        Assert.Equal("Install darktable and choose darktable-cli.exe in Develop settings. Real captures never fall back to a demo processor.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.PreviewAsync(frame.Id, null, default))).Message);
    }

    private async Task<(RollStore Store, RollCapture[] Frames)> Roll(int count = 1)
    {
        var store = RollStore.Create(Path.Combine(directory, "roll"), "Workflow test", FilmKind.ColorNegative, true);
        var source = new DemoCaptureSource();
        for (int number = 1; number <= count; number++)
        {
            var files = await source.CaptureAsync(CapturePurpose.Frame, CaptureMode.White, number,
                Path.Combine(directory, "incoming", number.ToString()), (_, _) => Task.CompletedTask, default);
            store.AddCapture(CapturePurpose.Frame, CaptureMode.White, files, true);
        }
        return (store, store.Document.Captures.ToArray());
    }

    private sealed class Callback<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class InvalidFirstExportProcessor(FrameSources sources) : IFrameProcessor
    {
        private readonly DemoFrameProcessor renderer = new(sources);
        private bool first = true;

        public Task<PreviewResult> PreviewAsync(RollDocument roll, RollCapture frame, DevelopRecipe recipe,
            string workDirectory, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken) =>
            renderer.PreviewAsync(roll, frame, recipe, workDirectory, progress, cancellationToken);

        public Task<FrameProcessingResult> ExportAsync(RollDocument roll, RollCapture frame, DevelopRecipe recipe,
            string outputDirectory, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
        {
            if (!first) return renderer.ExportAsync(roll, frame, recipe, outputDirectory, progress, cancellationToken);
            first = false;
            return Task.FromResult(new FrameProcessingResult([], []));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
