using System.Runtime.Versioning;
using FilmLight.Core;
using FilmLight.Workflow;
using Xunit;

namespace FilmLight.Tests;

[SupportedOSPlatform("windows")]
public sealed class CalibrationPipelineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FilmLight-calibration-pipeline-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EmptyRunnerReturnsInitialStateAndCustomStepsComposeInOrder()
    {
        using var sources = new FrameSources(root, null);
        var frame = Frame();
        var roll = new RollDocument { Captures = [frame] };
        var context = new CalibrationContext(roll, frame, roll.FilmKind, sources.Open(roll, frame), _ => { }, null);
        var initial = new CalibrationState(new());
        Assert.Same(initial, await CalibrationPipeline.RunAsync([], context, initial, default));
        var calls = new List<int>();
        var sample = new PictureSample(frame.Id, .1, .2, .8, .7, .6);
        ICalibrationStep[] steps =
        [
            new Step((_, state, _) =>
            {
                calls.Add(1);
                Assert.Same(initial, state);
                return Task.FromResult(state with { Picture = new() { Light = sample } });
            }),
            new Step((_, state, _) =>
            {
                calls.Add(2);
                Assert.Same(sample, state.Picture.Light);
                return Task.FromResult(state with { Picture = state.Picture with { FilmBase = sample } });
            })
        ];
        var result = await CalibrationPipeline.RunAsync(steps, context, initial, default);
        Assert.Equal([1, 2], calls);
        Assert.Same(sample, result.Picture.FilmBase);
    }

    [Fact]
    public async Task CancellationBeforeAndDuringStepsPropagatesWithoutRunningLaterSteps()
    {
        using var sources = new FrameSources(root, null);
        var frame = Frame();
        var roll = new RollDocument { Captures = [frame] };
        var context = new CalibrationContext(roll, frame, roll.FilmKind, sources.Open(roll, frame), _ => { }, null);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CalibrationPipeline.RunAsync([], context, new(new()), canceled.Token));
        using var during = new CancellationTokenSource();
        var canceling = new Step((_, state, _) =>
        {
            during.Cancel();
            return Task.FromResult(state);
        });
        var unexpected = new Step((_, _, _) => throw new Xunit.Sdk.XunitException("A later step ran after cancellation."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CalibrationPipeline.RunAsync([canceling, unexpected], context, new(new()), during.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CalibrationPipeline.RunAsync([canceling], context, new(new()), during.Token));
    }

    [Fact]
    public async Task RemovingDetectionProducesFrameLocalProvisionalOutcomeAndDoesNotReadInput()
    {
        using var sources = new FrameSources(root, null);
        var frame = Frame();
        var roll = new RollDocument { Captures = [frame] };
        var prepared = await FrameCalibration.PrepareAsync([new ClippingVerificationStep()], sources,
            roll, frame, new() { Accepted = true }, root, default);
        Assert.Equal(frame.Id, prepared.Recipe.FrameId);
        Assert.NotNull(prepared.Recipe.PictureCalibration);
        Assert.False(prepared.Recipe.PictureCalibration.IsComplete(roll.FilmKind));
        Assert.False(prepared.Recipe.Accepted);
        Assert.Null(prepared.Recipe.AcceptedAt);
        Assert.Contains("uncalibrated", prepared.Warning);
        Assert.Contains("Diagnostics:", prepared.Warning);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "calibration-diagnostics"), "auto-*.log"));
        Assert.Equal((prepared.Recipe, (string?)null), await FrameCalibration.PrepareAsync([],
            sources, roll, frame, prepared.Recipe, root, default));
    }

    private static RollCapture Frame() => new(Guid.NewGuid(), 1, CapturePurpose.Frame, CaptureMode.White,
        [], false, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(CalibrationTarget.Backlight, 11, false)]
    [InlineData(CalibrationTarget.Backlight, 12, false)]
    [InlineData(CalibrationTarget.FilmBase, 12, false)]
    [InlineData(CalibrationTarget.Backlight, 12, true)]
    public async Task VerificationRetainsDetectorFivePercentRuleAndNeverChangesSamples(
        CalibrationTarget target, int badPoints, bool dark)
    {
        Directory.CreateDirectory(root);
        const int edge = 256;
        var image = new LinearImage(edge, edge, Enumerable.Repeat(.95f, edge * edge * 3).ToArray());
        for (int n = 0; n < badPoints; n++)
        {
            int x = (int)((.25 + .5 * (n % 15 + .5) / 15) * edge);
            int y = (int)((.25 + .5 * (n / 15 + .5) / 15) * edge);
            image.Pixels[(y * edge + x) * 3] = dark ? 0 : 1;
        }
        string path = Path.Combine(root, "input.tif");
        ProcessingImages.WriteTiff(path, image, true, ProcessingImages.SyntheticLabel + "; " + ProcessingImages.LinearLabel,
            default, ProcessingImages.LinearRec2020Profile());
        var frame = Frame() with { IsDemo = true, Files = [new(LightChannel.White, path)] };
        var roll = new RollDocument { IsDemo = true, Captures = [frame] };
        var sample = new PictureSample(frame.Id, .5, .5, .95, .95, .95) { SourceWidth = 128, SourceHeight = 128 };
        var evidence = new SampleEvidence(.25, .25, 128, .95);
        var initial = target == CalibrationTarget.Backlight
            ? new CalibrationState(new() { Light = sample }) { LightEvidence = evidence }
            : new CalibrationState(new() { FilmBase = sample }) { FilmBaseEvidence = evidence };
        using var sources = new FrameSources(Path.Combine(root, "cache"), null);
        var context = new CalibrationContext(roll, frame, roll.FilmKind, sources.Open(roll, frame), _ => { }, null);
        if (badPoints <= 225 * .05)
            Assert.Same(initial, await CalibrationPipeline.RunAsync([new ClippingVerificationStep()], context, initial, default));
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                CalibrationPipeline.RunAsync([new ClippingVerificationStep()], context, initial, default));
            Assert.Contains(target == CalibrationTarget.FilmBase ? "Clear film patches"
                : dark ? "Backlight samples are too dark" : "Reduce camera exposure and recapture", error.Message);
        }
    }

    private sealed class Step(Func<CalibrationContext, CalibrationState, CancellationToken, Task<CalibrationState>> apply) : ICalibrationStep
    {
        public Task<CalibrationState> ApplyAsync(CalibrationContext context, CalibrationState state, CancellationToken token) =>
            apply(context, state, token);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
