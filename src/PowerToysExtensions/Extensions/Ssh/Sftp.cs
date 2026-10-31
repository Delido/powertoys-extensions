using System.Globalization;
using System.Text.RegularExpressions;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.Ssh;

public sealed record RemoteEntry(string Name, string Path, bool IsDir, bool IsLink, long Size, string Modified, string Permissions);

/// <summary>
/// SFTP through Windows' OpenSSH sftp.exe in batch mode: the whole ssh_config (ProxyJump, identity files),
/// the ssh-agent and known_hosts work exactly like in the terminal. Batch mode cannot ask for passwords,
/// so password-only hosts are opened interactively in the terminal instead.
/// </summary>
public static partial class Sftp
{
    private static readonly string Exe = File.Exists(@"C:\Windows\System32\OpenSSH\sftp.exe") ? @"C:\Windows\System32\OpenSSH\sftp.exe" : "sftp.exe";

    public static string Join(string dir, string name) => dir.EndsWith('/') ? dir + name : $"{dir}/{name}";

    public static string Parent(string path)
    {
        var trimmed = path.TrimEnd('/');
        var i = trimmed.LastIndexOf('/');
        return i <= 0 ? "/" : trimmed[..i];
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    /// <summary>Lists a folder; dir null = the home folder. Returns the resolved path and the entries.</summary>
    public static async Task<(string Path, RemoteEntry[] Entries)> List(string alias, string? dir, CancellationToken token)
    {
        var output = await Run(alias, $"{(dir is null ? string.Empty : $"cd {Quote(dir)}\n")}pwd\nls -la\n", token);
        var cwd = PwdLine().Match(output) is { Success: true } m ? m.Groups[1].Value.Trim() : dir ?? "/";
        var entries = output.Split('\n')
            .Select(l => LsLine().Match(l.TrimEnd('\r')))
            .Where(x => x.Success && x.Groups["name"].Value is not "." and not "..")
            .Select(x =>
            {
                var perms = x.Groups["perms"].Value;
                var name = x.Groups["name"].Value;
                var link = perms.StartsWith('l');
                if (link && name.Contains(" -> ", StringComparison.Ordinal))
                {
                    name = name[..name.IndexOf(" -> ", StringComparison.Ordinal)];
                }

                return new RemoteEntry(name, Join(cwd, name), perms.StartsWith('d') || (link && name.EndsWith('/')), link, long.Parse(x.Groups["size"].Value, CultureInfo.InvariantCulture), x.Groups["date"].Value, perms);
            })
            .OrderByDescending(e => e.IsDir)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return (cwd, entries);
    }

    public static Task Download(string alias, string remote, string local) => Run(alias, $"get {Quote(remote)} {Quote(local)}\n", CancellationToken.None, TimeSpan.FromMinutes(30));

    public static Task Upload(string alias, string local, string remoteDir) => Run(alias, $"put {Quote(local)} {Quote(Join(remoteDir, Path.GetFileName(local)))}\n", CancellationToken.None, TimeSpan.FromMinutes(30));

    public static Task MakeDir(string alias, string path) => Run(alias, $"mkdir {Quote(path)}\n", CancellationToken.None);

    public static Task Rename(string alias, string from, string to) => Run(alias, $"rename {Quote(from)} {Quote(to)}\n", CancellationToken.None);

    public static Task Delete(string alias, RemoteEntry entry) => Run(alias, $"{(entry.IsDir ? "rmdir" : "rm")} {Quote(entry.Path)}\n", CancellationToken.None);

    /// <summary>Downloads the first part of a file for the preview.</summary>
    public static async Task<string> Head(string alias, RemoteEntry entry, int maxBytes = 64 * 1024)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"sftp-preview-{Guid.NewGuid():N}");
        try
        {
            await Download(alias, entry.Path, tmp);
            await using var stream = File.OpenRead(tmp);
            var buffer = new byte[Math.Min(maxBytes, stream.Length)];
            _ = await stream.ReadAsync(buffer);
            return System.Text.Encoding.UTF8.GetString(buffer);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private static string Quote(string path) => $"\"{path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static async Task<string> Run(string alias, string commands, CancellationToken token, TimeSpan? timeout = null)
    {
        Terminal.SafeArg(alias);
        var result = await Proc.Run(Exe, ["-q", "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", "-b", "-", alias], timeout ?? TimeSpan.FromMinutes(1), token, commands);
        if (result.ExitCode != 0)
        {
            var error = result.Error.Trim().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var message = error.LastOrDefault() ?? $"sftp exited with {result.ExitCode}";
            if (error.Any(l => l.Contains("Permission denied", StringComparison.Ordinal) || l.Contains("Host key verification failed", StringComparison.Ordinal)))
            {
                message += " – connect once in the terminal (password, new host key); batch mode only works with keys or the ssh-agent";
            }

            throw new InvalidOperationException(message);
        }

        return result.Output;
    }

    [GeneratedRegex(@"Remote working directory:\s*(.+)")]
    private static partial Regex PwdLine();

    [GeneratedRegex(@"^(?<perms>[dlcbps-][rwxsStT-]{9}\S*)\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+(?<date>\w{3}\s+\d+\s+[\d:]+)\s+(?<name>.+)$")]
    private static partial Regex LsLine();
}
