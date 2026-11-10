using FilmLight.Workflow;
using Xunit;

namespace FilmLight.Tests;

public sealed class DevelopPipelineTests
{
    [Fact]
    public async Task CustomStepsRunInOrderAndAnEmptyListReturnsInitialState()
    {
        using var sources = new FrameSources(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), null);
        var frame = new RollCapture(Guid.NewGuid(), 1, CapturePurpose.Frame, CaptureMode.White, [], true, DateTimeOffset.UtcNow);
        var context = new DevelopContext(new(), frame, new(), DevelopIntent.Preview, sources, "", null);
        var initial = new DevelopState(new(1, 1, [.1f, .2f, .3f]), [1, 1, 1], new());
        Assert.Same(initial, await DevelopPipeline.RunAsync([], context, initial, default));
        var calls = new List<int>();
        var result = await DevelopPipeline.RunAsync([
            new Step((_, state, _) =>
            {
                calls.Add(1);
                return Task.FromResult(state with { FilmBase = [.5f, .5f, .5f] });
            }),
            new Step((_, state, _) =>
            {
                calls.Add(2);
                Assert.Equal(.5f, state.FilmBase[0]);
                return Task.FromResult(state with { Controls = state.Controls with { ExposureEv = 1 } });
            })], context, initial, default);
        Assert.Equal([1, 2], calls);
        Assert.Equal(1, result.Controls.ExposureEv);
    }

    [Fact]
    public async Task CancellationBeforeDuringAndAfterStepsPropagates()
    {
        using var sources = new FrameSources(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), null);
        var frame = new RollCapture(Guid.NewGuid(), 1, CapturePurpose.Frame, CaptureMode.White, [], true, DateTimeOffset.UtcNow);
        var context = new DevelopContext(new(), frame, new(), DevelopIntent.Preview, sources, "", null);
        var initial = new DevelopState(new(1, 1, [0, 0, 0]), [1, 1, 1], new());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DevelopPipeline.RunAsync([], context, initial, canceled.Token));
        foreach (bool last in new[] { false, true })
        {
            using var during = new CancellationTokenSource();
            var cancel = new Step((_, state, _) => { during.Cancel(); return Task.FromResult(state); });
            var unexpected = new Step((_, _, _) => throw new Xunit.Sdk.XunitException("Step ran after cancellation."));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                DevelopPipeline.RunAsync(last ? [cancel] : [cancel, unexpected], context, initial, during.Token));
        }
    }

    [Fact]
    public void InversionCacheBoundsRecentEntriesAndCanceledWritesLeaveNoPartialFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "FilmLight-inversion-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new PreviewInversionCache(directory);
            var image = new LinearImage(1, 1, [.1f, .2f, .3f]);
            for (int i = 0; i < 8; i++)
            {
                cache.Write(i.ToString(), image, default);
                File.SetLastWriteTimeUtc(Path.Combine(directory, i + ".pfm"), DateTime.UtcNow.AddMinutes(i - 10));
            }
            Assert.True(cache.TryRead("0", 1, 1, default, out var reused));
            Assert.Equal(image.Pixels, reused!.Pixels);
            cache.Write("8", image, default);
            Assert.Equal(8, Directory.GetFiles(directory, "*.pfm").Length);
            Assert.True(File.Exists(Path.Combine(directory, "0.pfm")));
            Assert.False(File.Exists(Path.Combine(directory, "1.pfm")));
            Assert.False(cache.TryRead("0", 2, 1, default, out _));
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => cache.Write("canceled", image, cancel.Token));
            Assert.False(File.Exists(Path.Combine(directory, "canceled.pfm")));
            Assert.Empty(Directory.GetFiles(directory, "*.partial"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class Step(Func<DevelopContext, DevelopState, CancellationToken, Task<DevelopState>> apply) : IDevelopStep
    {
        public Task<DevelopState> ApplyAsync(DevelopContext context, DevelopState state, CancellationToken token) =>
            apply(context, state, token);
    }
}
