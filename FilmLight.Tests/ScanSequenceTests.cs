using FilmLight.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FilmLight.Tests;

public sealed class ScanSequenceTests
{
    [Fact]
    public async Task CountdownThenThreeFullIntervalsLeavesBlue()
    {
        var clock = new FakeTimeProvider();
        var lights = new List<LightChannel>();
        var transitions = new List<DateTimeOffset>();
        var progress = new List<ScanProgress>();
        var started = clock.GetUtcNow();
        var sequence = new ScanSequence(clock);
        var run = sequence.RunAsync(new(2, false, 3), progress.Add, (channel, _) =>
        {
            lights.Add(channel);
            transitions.Add(clock.GetUtcNow());
            return Task.CompletedTask;
        }, null, CancellationToken.None);
        Assert.Empty(lights);
        await AdvanceUntil(clock, () => lights.Count == 1, 30);
        Assert.Equal([LightChannel.Red], lights);
        await AdvanceUntil(clock, () => lights.Count == 2, 20);
        Assert.Equal([LightChannel.Red, LightChannel.Green], lights);
        await AdvanceUntil(clock, () => lights.Count == 3, 20);
        Assert.False(run.IsCompleted);
        await AdvanceUntil(clock, () => run.IsCompleted, 20);
        await run;
        Assert.Equal(TimeSpan.FromSeconds(3), transitions[0] - started);
        Assert.Equal(TimeSpan.FromSeconds(2), transitions[1] - transitions[0]);
        Assert.Equal(TimeSpan.FromSeconds(2), transitions[2] - transitions[1]);
        Assert.Equal(TimeSpan.FromSeconds(9), clock.GetUtcNow() - started);
        Assert.Equal(ScanPhase.Complete, progress[^1].Phase);
        Assert.Equal(LightChannel.Blue, progress[^1].Channel);
    }

    [Fact]
    public async Task RepeatReturnsToRedAndCancellationFreezesLight()
    {
        var clock = new FakeTimeProvider();
        var lights = new List<LightChannel>();
        using var cancellation = new CancellationTokenSource();
        var run = new ScanSequence(clock).RunAsync(new(1, true, 0), _ => { }, (channel, _) =>
        {
            lights.Add(channel);
            return Task.CompletedTask;
        }, null, cancellation.Token);
        await AdvanceUntil(clock, () => lights.Count == 4, 30);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal([LightChannel.Red, LightChannel.Green, LightChannel.Blue, LightChannel.Red], lights);
    }

    [Fact]
    public async Task CancellationDuringCountdownNeverChangesLight()
    {
        using var cancellation = new CancellationTokenSource();
        var lights = new List<LightChannel>();
        var run = new ScanSequence(new FakeTimeProvider()).RunAsync(new(), _ => { }, (channel, _) =>
        {
            lights.Add(channel);
            return Task.CompletedTask;
        }, null, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(lights);
    }

    [Fact]
    public async Task CameraCompletionAndDownloadedPathsPrecedeNextColor()
    {
        var camera = new ControlledCamera();
        var lights = new List<LightChannel>();
        var files = new List<string>();
        var run = new ScanSequence(camera: camera).RunAsync(new(1, false, 0), _ => { }, (channel, _) =>
        {
            lights.Add(channel);
            return Task.CompletedTask;
        }, (_, result) => files.AddRange(result.FilePaths), CancellationToken.None);
        Assert.Equal([LightChannel.Red], lights);
        for (var i = 0; i < 3; i++)
        {
            var pending = camera.Pending;
            pending.SetResult(new([$"capture-{i}.RAF"]));
            await WaitUntil(() => run.IsCompleted || camera.Pending != pending);
        }
        await run;
        Assert.Equal([LightChannel.Red, LightChannel.Green, LightChannel.Blue], lights);
        Assert.Equal(["capture-0.RAF", "capture-1.RAF", "capture-2.RAF"], files);
    }

    [Fact]
    public async Task CameraFailureDoesNotAdvanceColor()
    {
        var camera = new ControlledCamera();
        var lights = new List<LightChannel>();
        var run = new ScanSequence(camera: camera).RunAsync(new(1, false, 0), _ => { }, (channel, _) =>
        {
            lights.Add(channel);
            return Task.CompletedTask;
        }, null, CancellationToken.None);
        camera.Pending.SetException(new IOException("Camera disconnected"));
        await Assert.ThrowsAsync<IOException>(() => run);
        Assert.Equal([LightChannel.Red], lights);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public async Task RejectsInvalidIntervals(int interval)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ScanSequence().RunAsync(
            new(interval), _ => { }, (_, _) => Task.CompletedTask, null, CancellationToken.None));
    }

    private static async Task AdvanceUntil(FakeTimeProvider clock, Func<bool> condition, int maximumSteps)
    {
        for (var i = 0; i < maximumSteps && !condition(); i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10);
        }
        Assert.True(condition(), "Sequence did not advance within the expected interval.");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Camera completion did not advance the sequence.");
    }

    private sealed class ControlledCamera : ICameraCapture
    {
        public TaskCompletionSource<CaptureResult> Pending { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CaptureResult> CaptureAsync(LightChannel channel, CancellationToken cancellationToken)
        {
            Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return Pending.Task.WaitAsync(cancellationToken);
        }
    }
}
