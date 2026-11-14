using FilmLight.Core;
using System.Text.Json;
using Xunit;

namespace FilmLight.Tests;

public sealed class RollSessionTests : IDisposable
{
    private readonly string output = Path.Combine(Path.GetTempPath(), $"FilmLight-roll-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReconnectKeepsReferencesAndResumesOnlyMissingFrameColors()
    {
        var roll = RollSession.Create(output);
        var references = roll.BeginCapture(RollCaptureKind.References);
        foreach (var channel in new[] { LightChannel.Red, LightChannel.Green, LightChannel.Blue })
            roll.RecordCapture(channel, Save(roll, references, channel));
        var referencePaths = references.Channels.Values.SelectMany(files => files).ToArray();
        var frame = roll.BeginCapture(RollCaptureKind.Frame);
        var attempts = new List<LightChannel>();
        var camera = new FileCamera(roll, frame, attempts, failGreen: true);

        await Assert.ThrowsAsync<IOException>(() => Run(roll, camera));
        Assert.Single(frame.Channels);

        var restored = RollSession.Load(roll.DirectoryPath);
        Assert.True(restored.HasReferences);
        Assert.Equal(frame.Id, restored.BeginCapture(RollCaptureKind.Frame).Id);
        Assert.Equal(referencePaths, restored.Captures[0].Channels.Values.SelectMany(files => files));
        await Run(restored, new FileCamera(restored, restored.Pending!, attempts, failGreen: false));

        Assert.Equal([LightChannel.Red, LightChannel.Green, LightChannel.Green, LightChannel.Blue], attempts);
        Assert.Null(restored.Pending);
        Assert.Equal(1, restored.CompletedFrames);
        Assert.All(referencePaths, path => Assert.True(File.Exists(path)));
        Assert.True(RollSession.Load(restored.DirectoryPath).HasReferences);
    }

    [Fact]
    public void NewRollCreatesSeparateFolderWithoutChangingOldReferences()
    {
        var first = RollSession.Create(output);
        var capture = first.BeginCapture(RollCaptureKind.References);
        foreach (var channel in new[] { LightChannel.Red, LightChannel.Green, LightChannel.Blue })
            first.RecordCapture(channel, Save(first, capture, channel));
        var second = RollSession.Create(output);

        Assert.NotEqual(first.DirectoryPath, second.DirectoryPath);
        Assert.True(Directory.Exists(second.DirectoryPath));
        Assert.Empty(second.Captures);
        Assert.False(second.HasReferences);
        Assert.True(RollSession.Load(first.DirectoryPath).HasReferences);
    }

    [Fact]
    public void CannotReplaceReferencesOrSwitchAnUnfinishedCapture()
    {
        var roll = RollSession.Create(output);
        var references = roll.BeginCapture(RollCaptureKind.References);
        Assert.Throws<InvalidOperationException>(() => roll.BeginCapture(RollCaptureKind.Frame));
        foreach (var channel in new[] { LightChannel.Red, LightChannel.Green, LightChannel.Blue })
            roll.RecordCapture(channel, Save(roll, references, channel));
        Assert.Throws<InvalidOperationException>(() => roll.BeginCapture(RollCaptureKind.References));
        roll.BeginCapture(RollCaptureKind.Frame);
        Assert.True(roll.HasReferences);
    }

    [Fact]
    public void MissingReferenceFileIsReportedInsteadOfSilentlySkippingIt()
    {
        var roll = RollSession.Create(output);
        var references = roll.BeginCapture(RollCaptureKind.References);
        var result = Save(roll, references, LightChannel.Red);
        roll.RecordCapture(LightChannel.Red, result);
        File.Delete(result.FilePaths[0]);

        Assert.Throws<InvalidDataException>(() => RollSession.Load(roll.DirectoryPath));
    }

    [Theory]
    [InlineData(RollCaptureKind.References)]
    [InlineData(RollCaptureKind.Frame)]
    public void RetakeKeepsRejectedFilesAndRestartsAllColorsWithNewScanId(RollCaptureKind kind)
    {
        var roll = RollSession.Create(output);
        var original = roll.BeginCapture(kind);
        foreach (var channel in new[] { LightChannel.Red, LightChannel.Green, LightChannel.Blue })
            roll.RecordCapture(channel, Save(roll, original, channel));
        var files = original.Channels.Values.SelectMany(paths => paths).ToArray();

        var replacement = roll.RetakeCapture(original.Id);
        var restored = RollSession.Load(roll.DirectoryPath);

        Assert.NotEqual(original.Id, replacement.Id);
        Assert.Equal(original.FrameNumber, replacement.FrameNumber);
        Assert.Equal(replacement.Id, restored.Pending!.Id);
        Assert.Empty(restored.Pending.Channels);
        Assert.True(restored.Captures[0].Discarded);
        Assert.False(restored.HasReferences);
        Assert.Equal(0, restored.CompletedFrames);
        Assert.All(files, file => Assert.True(File.Exists(file)));
        foreach (var channel in new[] { LightChannel.Red, LightChannel.Green, LightChannel.Blue })
            restored.RecordCapture(channel, Save(restored, restored.Pending!, channel));
        Assert.Equal(kind == RollCaptureKind.References, restored.HasReferences);
        Assert.Equal(kind == RollCaptureKind.Frame ? 1 : 0, restored.CompletedFrames);
    }

    [Fact]
    public void RetakingOlderFramePreservesLaterFrameNumbersAndReferences()
    {
        var roll = RollSession.Create(output);
        var references = Complete(roll, RollCaptureKind.References);
        var first = Complete(roll, RollCaptureKind.Frame);
        var second = Complete(roll, RollCaptureKind.Frame);

        var replacement = roll.RetakeCapture(first.Id);
        Assert.Equal(1, replacement.FrameNumber);
        Assert.Equal(2, second.FrameNumber);
        Assert.True(roll.HasReferences);
        Assert.False(roll.Captures.Single(capture => capture.Id == references.Id).Discarded);
        roll.RecordCapture(LightChannel.Red, Save(roll, replacement, LightChannel.Red));
        Assert.Throws<InvalidOperationException>(() => roll.RetakeCapture(second.Id));
        Assert.False(roll.Captures.Single(capture => capture.Id == second.Id).Discarded);
        foreach (var channel in new[] { LightChannel.Green, LightChannel.Blue })
            roll.RecordCapture(channel, Save(roll, replacement, channel));
        Assert.Equal(3, roll.BeginCapture(RollCaptureKind.Frame).FrameNumber);
    }

    [Fact]
    public void CancelingPartialFrameDiscardsItsSavedColorsWithoutLosingReferences()
    {
        var roll = RollSession.Create(output);
        Complete(roll, RollCaptureKind.References);
        var frame = roll.BeginCapture(RollCaptureKind.Frame);
        var red = Save(roll, frame, LightChannel.Red);
        roll.RecordCapture(LightChannel.Red, red);

        var replacement = roll.RetakeCapture(frame.Id);
        Assert.Empty(replacement.Channels);
        Assert.True(roll.HasReferences);
        Assert.True(File.Exists(red.FilePaths[0]));
        Assert.Equal(frame.FrameNumber, RollSession.Load(roll.DirectoryPath).Pending!.FrameNumber);
    }

    [Fact]
    public void CanRetakeReferencesAfterCancelingAnUnstartedFrameRetake()
    {
        var roll = RollSession.Create(output);
        var references = Complete(roll, RollCaptureKind.References);
        var frame = roll.BeginCapture(RollCaptureKind.Frame);
        roll.RecordCapture(LightChannel.Red, Save(roll, frame, LightChannel.Red));
        var canceledFrame = roll.RetakeCapture(frame.Id);

        var replacement = roll.RetakeCapture(references.Id);

        Assert.Equal(RollCaptureKind.References, replacement.Kind);
        Assert.False(roll.HasReferences);
        Assert.True(roll.Captures.Single(capture => capture.Id == canceledFrame.Id).Discarded);
        Assert.Equal(replacement.Id, RollSession.Load(roll.DirectoryPath).Pending!.Id);
        foreach (var channel in new[] { LightChannel.Red, LightChannel.Green, LightChannel.Blue })
            roll.RecordCapture(channel, Save(roll, replacement, channel));
        Assert.Equal(frame.FrameNumber, roll.BeginCapture(RollCaptureKind.Frame).FrameNumber);
    }

    [Fact]
    public void ExistingRollWithoutFrameNumbersRestoresSequentialNumbers()
    {
        var roll = RollSession.Create(output);
        Complete(roll, RollCaptureKind.References);
        Complete(roll, RollCaptureKind.Frame);
        Complete(roll, RollCaptureKind.Frame);
        File.WriteAllText(Path.Combine(roll.DirectoryPath, "roll.json"), JsonSerializer.Serialize(
            roll.Captures.Select(capture => new { capture.Id, capture.Kind, capture.Channels })));

        var restored = RollSession.Load(roll.DirectoryPath);

        Assert.Equal([1, 2], restored.Captures.Where(capture => capture.Kind == RollCaptureKind.Frame)
            .Select(capture => capture.FrameNumber));
        Assert.True(restored.HasReferences);
        Assert.Equal(3, restored.BeginCapture(RollCaptureKind.Frame).FrameNumber);
    }

    private static RollCapture Complete(RollSession roll, RollCaptureKind kind)
    {
        var capture = roll.BeginCapture(kind);
        foreach (var channel in new[] { LightChannel.Red, LightChannel.Green, LightChannel.Blue })
            roll.RecordCapture(channel, Save(roll, capture, channel));
        return capture;
    }

    private static Task Run(RollSession roll, ICameraCapture camera) =>
        new ScanSequence(camera: camera, completedChannels: roll.Pending!.Channels.Keys).RunAsync(
            new(1, false, 0), _ => { }, (_, _) => Task.CompletedTask,
            roll.RecordCapture, CancellationToken.None);

    private static CaptureResult Save(RollSession roll, RollCapture capture, LightChannel channel)
    {
        var folder = Path.Combine(roll.DirectoryPath, $"Scan-{capture.Id}", channel.ToString());
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{channel}.RAF");
        File.WriteAllText(path, "captured image");
        return new([path]);
    }

    private sealed class FileCamera(RollSession roll, RollCapture capture, List<LightChannel> attempts,
        bool failGreen) : ICameraCapture
    {
        public Task<CaptureResult> CaptureAsync(LightChannel channel, CancellationToken cancellationToken)
        {
            attempts.Add(channel);
            return failGreen && channel == LightChannel.Green
                ? Task.FromException<CaptureResult>(new IOException("USB disconnected"))
                : Task.FromResult(Save(roll, capture, channel));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(output)) Directory.Delete(output, true);
    }
}
