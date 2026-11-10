using System.Diagnostics;

namespace FilmLight.Workflow;

internal sealed class DarktableProcess(string executable)
{
    internal async Task<string> RunAsync(IEnumerable<string> arguments, string directory, string name, CancellationToken token,
        bool writeLogs = true)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (writeLogs)
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".command.txt"),
                executable + Environment.NewLine + string.Join(Environment.NewLine, start.ArgumentList), token).ConfigureAwait(false);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException($"Could not start darktable-cli: {executable}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var stdout = DrainAsync(process.StandardOutput, writeLogs ? Path.Combine(directory, name + ".stdout.log") : null);
        var stderr = DrainAsync(process.StandardError, writeLogs ? Path.Combine(directory, name + ".stderr.log") : null);
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
        string[] logs = await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        if (writeLogs)
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".exit.txt"), process.ExitCode.ToString(),
                CancellationToken.None).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string error = logs[1];
            string location = writeLogs ? $" Inspect {Path.Combine(directory, name + ".stderr.log")}." : "";
            throw new IOException($"darktable-cli exited {process.ExitCode}.{location}\n{error[^Math.Min(error.Length, 2000)..]}");
        }
        return logs[0];
    }

    private static async Task<string> DrainAsync(StreamReader reader, string? path)
    {
        await using var file = path is null ? null : new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using var writer = file is null ? null : new StreamWriter(file);
        var text = new System.Text.StringBuilder();
        char[] buffer = new char[4096];
        int count, total = 0;
        const int limit = 8 * 1024 * 1024;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            if (total < limit)
            {
                int length = Math.Min(count, limit - total);
                text.Append(buffer, 0, length);
                if (writer is not null) await writer.WriteAsync(buffer.AsMemory(0, length)).ConfigureAwait(false);
            }
            total = Math.Min(limit + 1, total + count);
        }
        if (total > limit) throw new IOException($"darktable log exceeded {limit} characters: {path}");
        return text.ToString();
    }
}
