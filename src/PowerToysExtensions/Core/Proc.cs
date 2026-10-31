using System.Diagnostics;
using System.Text;

namespace PowerToysExtensions.Core;

/// <summary>Runs external programs hidden and collects their output.</summary>
public static class Proc
{
    public sealed record Result(int ExitCode, string Output, string Error);

    public static async Task<Result> Run(string exe, IEnumerable<string> args, TimeSpan? timeout = null, CancellationToken token = default, string? stdin = null)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{exe} could not be started");
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already gone
            }

            if (token.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"{Path.GetFileName(exe)} did not finish in time");
        }

        return new Result(process.ExitCode, await output, await error);
    }

    /// <summary>Starts a program detached (terminal, browser …) without waiting.</summary>
    public static void Start(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        Process.Start(info)?.Dispose();
    }

    /// <summary>Opens a file or URL with its default program.</summary>
    public static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    /// <summary>Finds a program in PATH and a few well-known install folders.</summary>
    public static string? Find(string exe, params string[] extraFolders)
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Concat(extraFolders)
            .Where(f => f.Length > 0);
        return folders.Select(f => Path.Combine(f, exe)).FirstOrDefault(File.Exists);
    }
}
