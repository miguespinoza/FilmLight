namespace FilmLight.Core;

public enum LightChannel { Red, Green, Blue, White }
public enum ScanPhase { Countdown, Holding, Complete }

public sealed record ScanOptions(int IntervalSeconds = 5, bool Repeat = false, int CountdownSeconds = 3);
public sealed record ScanProgress(ScanPhase Phase, LightChannel? Channel, int SetNumber, TimeSpan Remaining);
public sealed record CaptureResult(IReadOnlyList<string> FilePaths);

/// <summary>
/// An adapter must settle the displayed light before exposing, and return only when
/// exposure and requested image transfers finish. Cancellation must not leave a pending
/// exposure that can occur after the caller changes the light.
/// </summary>
public interface ICameraCapture
{
    Task<CaptureResult> CaptureAsync(LightChannel channel, CancellationToken cancellationToken);
}

public sealed class ScanSequence(TimeProvider? timeProvider = null, ICameraCapture? camera = null,
    IEnumerable<LightChannel>? completedChannels = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly HashSet<LightChannel> completed = completedChannels?.ToHashSet() ?? [];

    public async Task RunAsync(
        ScanOptions options,
        Action<ScanProgress> report,
        Func<LightChannel, CancellationToken, Task> showLight,
        Action<LightChannel, CaptureResult>? captureCompleted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(showLight);
        if (options.IntervalSeconds is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(options), "Interval must be 1 to 120 seconds.");
        if (options.CountdownSeconds is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(options), "Countdown must be 0 to 10 seconds.");

        await HoldAsync(ScanPhase.Countdown, null, 1, TimeSpan.FromSeconds(options.CountdownSeconds), report, cancellationToken);
        var set = 1;
        do
        {
            foreach (var channel in new[] { LightChannel.Red, LightChannel.Green, LightChannel.Blue })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (camera is not null && completed.Contains(channel))
                    continue;
                await showLight(channel, cancellationToken);
                if (camera is null)
                {
                    await HoldAsync(ScanPhase.Holding, channel, set, TimeSpan.FromSeconds(options.IntervalSeconds), report, cancellationToken);
                }
                else
                {
                    report(new(ScanPhase.Holding, channel, set, TimeSpan.Zero));
                    var result = await camera.CaptureAsync(channel, cancellationToken);
                    captureCompleted?.Invoke(channel, result);
                    completed.Add(channel);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            completed.Clear();
            if (!options.Repeat)
                break;
            set++;
        } while (true);
        report(new(ScanPhase.Complete, LightChannel.Blue, set, TimeSpan.Zero));
    }

    private async Task HoldAsync(ScanPhase phase, LightChannel? channel, int set, TimeSpan duration,
        Action<ScanProgress> report, CancellationToken cancellationToken)
    {
        var start = clock.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = duration - clock.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
                return;
            report(new(phase, channel, set, remaining));
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100),
                clock, cancellationToken);
        }
    }
}
