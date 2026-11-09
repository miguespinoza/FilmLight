using System.Text.Json;

namespace FilmLight.Workflow;

public sealed record RollLibraryEntry(string RootPath, RollDocument? Document,
    DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt, string? Error);

/// <summary>Discovers managed rolls and remembers linked rolls without duplicating their manifests.</summary>
public sealed class RollLibrary
{
    public RollLibrary(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
            throw new ArgumentException("The roll library path must be absolute.", nameof(rootPath));
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
    }

    public string RootPath { get; }
    private string LinksPath => Path.Combine(RootPath, "linked-rolls.json");

    public RollStore Create(FilmKind filmKind, bool isDemo)
    {
        var name = $"Roll {DateTime.Now:yyyy-MM-dd HHmmss}";
        return RollStore.Create(Path.Combine(RootPath, $"{name}-{Guid.NewGuid():N}"), name, filmKind, isDemo);
    }

    public IReadOnlyList<RollLibraryEntry> List(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootPath)) return [];
        var paths = Directory.EnumerateDirectories(RootPath)
            .Where(path => File.Exists(Path.Combine(path, "roll.json")))
            .Concat(ReadLinks()).Distinct(StringComparer.OrdinalIgnoreCase);
        var entries = new List<RollLibraryEntry>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var store = RollStore.Open(path);
                var document = store.Document;
                var manifest = Path.Combine(store.RootPath, "roll.json");
                var created = document.CreatedAt ?? document.Captures.Select(c => (DateTimeOffset?)c.CapturedAt).Min()
                    ?? new DateTimeOffset(File.GetCreationTimeUtc(manifest));
                entries.Add(new(store.RootPath, document, created,
                    new DateTimeOffset(File.GetLastWriteTimeUtc(manifest)), null));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                entries.Add(new(path, null, null, null, ex.Message));
            }
        }
        return entries.OrderBy(e => e.Error is not null).ThenByDescending(e => e.UpdatedAt)
            .ThenBy(e => e.RootPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public RollStore Link(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
            throw new ArgumentException("Enter the full path to an existing roll folder.", nameof(rootPath));
        var store = RollStore.Open(rootPath);
        Directory.CreateDirectory(RootPath);
        using var lease = new FileStream(Path.Combine(RootPath, ".library.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var paths = ReadLinks();
        if (paths.Contains(store.RootPath, StringComparer.OrdinalIgnoreCase)) return store;
        var temporary = Path.Combine(RootPath, $".linked-rolls-{Guid.NewGuid():N}.tmp");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, paths.Append(store.RootPath).ToArray());
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, LinksPath, overwrite: true);
        return store;
    }

    private string[] ReadLinks()
    {
        if (!File.Exists(LinksPath)) return [];
        try
        {
            using var stream = File.OpenRead(LinksPath);
            var paths = JsonSerializer.Deserialize<string[]>(stream);
            if (paths is null || paths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
                throw new InvalidDataException("The linked-roll list contains invalid folder paths.");
            return paths;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The linked-roll list is invalid.", ex);
        }
    }
}
