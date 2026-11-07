using System.Diagnostics;

namespace FilmLight.Workflow;

internal sealed class DarktableProcess(string executable)
{
    internal async Task<string> RunAsync(IEnumerable<string> arguments, string directory, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".command.txt"),
            executable + Environment.NewLine + string.Join(Environment.NewLine, start.ArgumentList), token).ConfigureAwait(false);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException($"Could not start darktable-cli: {executable}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var stdout = DrainAsync(process.StandardOutput, Path.Combine(directory, name + ".stdout.log"));
        var stderr = DrainAsync(process.StandardError, Path.Combine(directory, name + ".stderr.log"));
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            throw new TimeoutException($"darktable-cli exceeded two minutes; job artifacts/logs retained at {directory}.");
        }
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".exit.txt"), process.ExitCode.ToString(),
            CancellationToken.None).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string error = await File.ReadAllTextAsync(Path.Combine(directory, name + ".stderr.log"), token).ConfigureAwait(false);
            throw new IOException($"darktable-cli exited {process.ExitCode}. Inspect {Path.Combine(directory, name + ".stderr.log")}.\n{error[^Math.Min(error.Length, 2000)..]}");
        }
        string text = await File.ReadAllTextAsync(Path.Combine(directory, name + ".stdout.log"), token).ConfigureAwait(false);
        return text;
    }

    private static async Task DrainAsync(StreamReader reader, string path)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(file);
        char[] buffer = new char[4096];
        int count, total = 0;
        const int limit = 8 * 1024 * 1024;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            if (total < limit)
                await writer.WriteAsync(buffer.AsMemory(0, Math.Min(count, limit - total))).ConfigureAwait(false);
            total = Math.Min(limit + 1, total + count);
        }
        if (total > limit) throw new IOException($"darktable log exceeded {limit} characters: {path}");
    }
}
