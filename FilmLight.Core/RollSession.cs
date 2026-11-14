using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilmLight.Core;

public enum RollCaptureKind { References, Frame, White }

public sealed record RollCapture(string Id, RollCaptureKind Kind, Dictionary<LightChannel, string[]> Channels,
    int FrameNumber = 0, bool Discarded = false)
{
    [JsonIgnore] public bool Complete => Kind == RollCaptureKind.White
        ? Channels.ContainsKey(LightChannel.White)
        : Channels.ContainsKey(LightChannel.Red) && Channels.ContainsKey(LightChannel.Green)
            && Channels.ContainsKey(LightChannel.Blue);
}

public sealed class RollSession
{
    private readonly List<RollCapture> captures;
    public string DirectoryPath { get; }
    public IReadOnlyList<RollCapture> Captures => captures;
    public RollCapture? Pending => captures.LastOrDefault(capture => !capture.Discarded && !capture.Complete);
    public bool HasReferences => captures.Any(capture => !capture.Discarded && capture.Kind == RollCaptureKind.References && capture.Complete);
    public int CompletedFrames => captures.Count(capture => !capture.Discarded && capture.Kind == RollCaptureKind.Frame && capture.Complete);

    private RollSession(string directory, List<RollCapture> captures)
    {
        DirectoryPath = Path.GetFullPath(directory);
        this.captures = captures;
    }

    public static RollSession Create(string outputDirectory)
    {
        var roll = new RollSession(Path.Combine(outputDirectory,
            $"Roll-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"), []);
        Directory.CreateDirectory(roll.DirectoryPath);
        roll.Save();
        return roll;
    }

    public static RollSession Load(string directory)
    {
        var captures = JsonSerializer.Deserialize<List<RollCapture>>(File.ReadAllText(Path.Combine(directory, "roll.json")))
            ?? throw new InvalidDataException("Roll metadata is empty.");
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        if (captures.Any(capture => capture is null || !Guid.TryParseExact(capture.Id, "N", out _)
            || !Enum.IsDefined(capture.Kind) || capture.FrameNumber < 0 || capture.Channels is null
            || capture.Channels.Any(pair => !Enum.IsDefined(pair.Key) || pair.Value is not { Length: > 0 }
                || (capture.Kind == RollCaptureKind.White) != (pair.Key == LightChannel.White)
                || pair.Value.Any(file => string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file)
                    || (!capture.Discarded && !File.Exists(file))
                    || !Path.GetFullPath(file).StartsWith(root, StringComparison.OrdinalIgnoreCase))))
            || captures.Select(capture => capture.Id).Distinct().Count() != captures.Count
            || captures.Count(capture => !capture.Discarded && !capture.Complete) > 1
            || captures.Count(capture => !capture.Discarded && capture.Kind == RollCaptureKind.References) > 1)
            throw new InvalidDataException("Roll metadata contains invalid captures.");
        var frameNumber = 0;
        for (var i = 0; i < captures.Count; i++)
        {
            if (captures[i].Kind != RollCaptureKind.Frame) continue;
            if (captures[i].FrameNumber == 0)
                captures[i] = captures[i] with { FrameNumber = ++frameNumber };
            else frameNumber = Math.Max(frameNumber, captures[i].FrameNumber);
        }
        if (captures.Where(capture => !capture.Discarded && capture.Kind == RollCaptureKind.Frame)
            .GroupBy(capture => capture.FrameNumber).Any(group => group.Count() > 1))
            throw new InvalidDataException("Roll metadata contains duplicate frame numbers.");
        return new(directory, captures);
    }

    public RollCapture BeginCapture(RollCaptureKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (Pending is { } pending)
        {
            if (pending.Kind != kind)
                throw new InvalidOperationException("Resume the unfinished capture or start a new roll first.");
            return pending;
        }
        if (kind == RollCaptureKind.References && HasReferences)
            throw new InvalidOperationException("Reference photos are already saved for this roll.");
        var number = 0;
        if (kind == RollCaptureKind.Frame)
        {
            var activeNumbers = captures.Where(capture => !capture.Discarded && capture.Kind == kind)
                .Select(capture => capture.FrameNumber).ToHashSet();
            var next = captures.Select(capture => capture.FrameNumber).DefaultIfEmpty().Max() + 1;
            number = captures.Where(capture => capture.Discarded && capture.Kind == kind
                    && !activeNumbers.Contains(capture.FrameNumber))
                .Select(capture => capture.FrameNumber).DefaultIfEmpty(next).Min();
        }
        var capture = new RollCapture(Guid.NewGuid().ToString("N"), kind, [], number);
        captures.Add(capture);
        try { Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            captures.Remove(capture);
            throw;
        }
        return capture;
    }

    public RollCapture RetakeCapture(string captureId)
    {
        var index = captures.FindIndex(capture => capture.Id == captureId && !capture.Discarded);
        if (index < 0) throw new InvalidOperationException("Select a current roll capture to retake.");
        var original = captures[index];
        var pending = Pending;
        if (pending is not null && pending.Id != captureId && pending.Channels.Count > 0)
            throw new InvalidOperationException("Finish or cancel the unfinished capture before retaking another.");
        var abandonedIndex = pending is not null && pending.Id != captureId
            ? captures.FindIndex(capture => capture.Id == pending.Id) : -1;
        var replacement = new RollCapture(Guid.NewGuid().ToString("N"), original.Kind, [], original.FrameNumber);
        captures[index] = original with { Discarded = true };
        if (abandonedIndex >= 0 && pending is not null) captures[abandonedIndex] = pending with { Discarded = true };
        captures.Add(replacement);
        try { Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            captures.Remove(replacement);
            captures[index] = original;
            if (abandonedIndex >= 0 && pending is not null) captures[abandonedIndex] = pending;
            throw;
        }
        return replacement;
    }

    public void RecordCapture(LightChannel channel, CaptureResult result)
    {
        var capture = Pending ?? throw new InvalidOperationException("No roll capture is in progress.");
        if (!Enum.IsDefined(channel) || (capture.Kind == RollCaptureKind.White) != (channel == LightChannel.White)
            || capture.Channels.ContainsKey(channel))
            throw new InvalidOperationException("This channel does not belong to the unfinished capture.");
        if (result.FilePaths.Count == 0 || result.FilePaths.Any(file => !File.Exists(file)
            || !Path.GetFullPath(file).StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Capture files are missing or outside this roll.");
        capture.Channels.Add(channel, result.FilePaths.ToArray());
        try { Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            capture.Channels.Remove(channel);
            throw;
        }
    }

    private void Save()
    {
        var path = Path.Combine(DirectoryPath, "roll.json");
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, captures);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}
