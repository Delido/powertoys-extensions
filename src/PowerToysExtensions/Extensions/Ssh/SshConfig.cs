using System.Text.RegularExpressions;

namespace PowerToysExtensions.Extensions.Ssh;

public sealed class HostEntry
{
    public required string Alias { get; init; }

    public string? HostName { get; set; }

    public string? User { get; set; }

    public string? Port { get; set; }

    public string? ProxyJump { get; set; }

    public string? IdentityFile { get; set; }

    public string? Group { get; init; }

    public string[] Tags { get; init; } = [];

    public string? Description { get; init; }

    /// <summary>URL to open in the browser through the tunnel ("# web: …").</summary>
    public string? Web { get; init; }

    /// <summary>Local SOCKS port when the host is a tunnel (DynamicForward).</summary>
    public int? SocksPort { get; set; }

    /// <summary>True for "SessionType none" hosts (tunnel only, no shell).</summary>
    public bool TunnelOnly { get; set; }

    /// <summary>"local" or the source id.</summary>
    public required string Origin { get; init; }

    public required string File { get; init; }

    public Dictionary<string, string> Options { get; } = [];
}

/// <summary>
/// ~/.ssh/config plus team files synced into ~/.ssh/config.d (the same files the Raycast SSH extension
/// uses, so both launchers see the same hosts).
/// </summary>
public static partial class SshConfig
{
    public const string TeamPrefix = "raycast-team-";
    private const string BlockStart = "# >>> raycast-ssh (managed by the Raycast SSH extension) >>>";
    private const string BlockEnd = "# <<< raycast-ssh <<<";

    public static readonly string SshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
    public static readonly string ConfigPath = Path.Combine(SshDir, "config");
    public static readonly string ConfD = Path.Combine(SshDir, "config.d");
    public static readonly string DefaultsFile = Path.Combine(ConfD, "raycast-defaults.conf");

    /// <summary>
    /// Parses an ssh_config into concrete host entries (wildcard patterns are skipped). Metadata comes from
    /// comments: "# section: A / B" (until the next section), "# --- A / B ---", "# group:", "# tags:",
    /// "# description:", "# web:" (for the next Host block).
    /// </summary>
    public static List<HostEntry> Parse(string text, string origin, string file)
    {
        var entries = new List<HostEntry>();
        string? section = null, group = null, description = null, web = null;
        var tags = Array.Empty<string>();
        var current = new List<HostEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (Folder().Match(line) is { Success: true } folder)
            {
                section = folder.Groups[1].Value;
                continue;
            }

            if (Meta().Match(line) is { Success: true } meta)
            {
                var value = meta.Groups[2].Value.Trim();
                switch (meta.Groups[1].Value.ToLowerInvariant())
                {
                    case "section":
                        section = value.Length > 0 ? value : null;
                        break;
                    case "group":
                        group = value;
                        break;
                    case "web":
                        web = value;
                        break;
                    case "tags":
                        tags = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        break;
                    default:
                        description = value;
                        break;
                }

                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            if (KeyValue().Match(line) is not { Success: true } kv)
            {
                continue;
            }

            var key = kv.Groups[1].Value.ToLowerInvariant();
            var val = kv.Groups[2].Value.Trim().Trim('"');
            if (key == "host")
            {
                current = [.. val.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.IndexOfAny(['*', '?', '!']) < 0)
                    .Select(alias => new HostEntry { Alias = alias, Group = group ?? section, Tags = tags, Description = description, Web = web, Origin = origin, File = file })];
                entries.AddRange(current);
                (group, description, web, tags) = (null, null, null, []);
                continue;
            }

            if (key == "match")
            {
                current = [];
                continue;
            }

            foreach (var e in current)
            {
                // first value wins, like ssh
                if (!e.Options.TryAdd(key, val))
                {
                    continue;
                }

                switch (key)
                {
                    case "hostname":
                        e.HostName = val;
                        break;
                    case "user":
                        e.User = val;
                        break;
                    case "port":
                        e.Port = val;
                        break;
                    case "proxyjump":
                        e.ProxyJump = val;
                        break;
                    case "identityfile":
                        e.IdentityFile = val;
                        break;
                    case "dynamicforward" when int.TryParse(val.Split(':')[^1], out var port):
                        e.SocksPort = port;
                        break;
                    case "sessiontype":
                        e.TunnelOnly = val.Equals("none", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }
        }

        return entries;
    }

    /// <summary>Local hosts plus all synced team files; local entries win on duplicate aliases.</summary>
    public static (List<HostEntry> Hosts, HashSet<string> Overridden) Load()
    {
        var local = Parse(Read(ConfigPath) ?? string.Empty, "local", ConfigPath);
        var team = new List<HostEntry>();
        if (Directory.Exists(ConfD))
        {
            foreach (var file in Directory.EnumerateFiles(ConfD, $"{TeamPrefix}*.conf").Order())
            {
                var name = Path.GetFileNameWithoutExtension(file);
                team.AddRange(Parse(Read(file) ?? string.Empty, name[TeamPrefix.Length..], file));
            }
        }

        var localAliases = local.Select(h => h.Alias).ToHashSet();
        var overridden = new HashSet<string>();
        var seen = new HashSet<string>();
        var hosts = new List<HostEntry>();
        foreach (var h in local.Concat(team))
        {
            if (!seen.Add(h.Alias))
            {
                if (h.Origin != "local" && localAliases.Contains(h.Alias))
                {
                    overridden.Add(h.Alias);
                }

                continue;
            }

            hosts.Add(h);
        }

        return (hosts, overridden);
    }

    public static bool IsIntegrated() => (Read(ConfigPath) ?? string.Empty).Contains(BlockStart, StringComparison.Ordinal);

    /// <summary>
    /// Adds the Include block to ~/.ssh/config (with a backup) before the first catch-all block. Precedence:
    /// own specific hosts, team hosts, personal defaults, own "Host *".
    /// </summary>
    public static void Integrate()
    {
        Directory.CreateDirectory(ConfD);
        if (!File.Exists(DefaultsFile))
        {
            File.WriteAllText(DefaultsFile, "# Personal defaults – managed by the SSH extension (Manage SSH Sources → Personal Defaults)\n");
        }

        var original = Read(ConfigPath) ?? string.Empty;
        if (original.Contains(BlockStart, StringComparison.Ordinal))
        {
            return;
        }

        var eol = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] block = [BlockStart, "Match all", $"Include config.d/{TeamPrefix}*.conf", "Include config.d/raycast-defaults.conf", BlockEnd];
        var lines = original.Length > 0 ? original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList() : [];
        var catchAll = lines.FindIndex(l => CatchAll().IsMatch(l));
        if (catchAll >= 0)
        {
            lines.InsertRange(catchAll, [.. block, string.Empty]);
        }
        else
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.AddRange([.. block, string.Empty]);
        }

        Directory.CreateDirectory(SshDir);
        if (original.Length > 0)
        {
            File.Copy(ConfigPath, $"{ConfigPath}.raycast-backup-{DateTime.Now:yyyy-MM-ddTHH-mm-ss}");
        }

        File.WriteAllText(ConfigPath, string.Join(eol, lines));
    }

    /// <summary>Comments out options that execute commands or load code; returns the cleaned text and the number of hits.</summary>
    public static (string Text, int Blocked) Sanitize(string text)
    {
        var blocked = 0;
        var cleaned = string.Join("\n", text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line =>
        {
            if (Risky().IsMatch(line) || RiskyMatch().IsMatch(line))
            {
                blocked++;
                return $"# [blocked by the SSH extension] {line.Trim()}";
            }

            return line;
        }));
        return (cleaned, blocked);
    }

    public static (string User, string IdentityFile, string Extra) ReadDefaults()
    {
        string user = string.Empty, identity = string.Empty;
        var extra = new List<string>();
        foreach (var line in (Read(DefaultsFile) ?? string.Empty).Split('\n').Select(l => l.Trim()))
        {
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("host ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (KeyValue().Match(line) is not { Success: true } m)
            {
                continue;
            }

            switch (m.Groups[1].Value.ToLowerInvariant())
            {
                case "user":
                    user = m.Groups[2].Value;
                    break;
                case "identityfile":
                    identity = m.Groups[2].Value.Trim('"');
                    break;
                default:
                    extra.Add(line);
                    break;
            }
        }

        return (user, identity, string.Join("\n", extra));
    }

    public static void WriteDefaults(string user, string identityFile, string extra)
    {
        Directory.CreateDirectory(ConfD);
        var lines = new List<string> { "# Personal defaults – managed by the SSH extension (Manage SSH Sources → Personal Defaults)", "Host *" };
        if (user.Trim().Length > 0)
        {
            lines.Add($"    User {user.Trim()}");
        }

        if (identityFile.Trim().Length > 0)
        {
            lines.Add($"    IdentityFile \"{identityFile.Trim()}\"");
        }

        lines.AddRange(extra.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => $"    {l}"));
        File.WriteAllText(DefaultsFile, string.Join("\n", lines) + "\n");
    }

    /// <summary>Appends a host block to the local ~/.ssh/config (before the include block / catch-all).</summary>
    public static void AppendLocalHost(string block)
    {
        var original = Read(ConfigPath) ?? string.Empty;
        var eol = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original.Length > 0 ? original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList() : [];
        var anchor = lines.FindIndex(l => l.StartsWith(BlockStart, StringComparison.Ordinal) || CatchAll().IsMatch(l));
        string[] blockLines = [.. block.TrimEnd().Split('\n'), string.Empty];
        if (anchor >= 0)
        {
            lines.InsertRange(anchor, blockLines);
        }
        else
        {
            lines.AddRange([string.Empty, .. blockLines]);
        }

        Directory.CreateDirectory(SshDir);
        if (original.Length > 0)
        {
            File.Copy(ConfigPath, $"{ConfigPath}.raycast-backup-last", overwrite: true);
        }

        File.WriteAllText(ConfigPath, string.Join(eol, lines));
    }

    /// <summary>Config block for sharing a host with the team (no keys).</summary>
    public static string Block(HostEntry h)
    {
        var names = new Dictionary<string, string> { ["hostname"] = "HostName", ["user"] = "User", ["port"] = "Port", ["proxyjump"] = "ProxyJump", ["forwardagent"] = "ForwardAgent", ["localforward"] = "LocalForward", ["remoteforward"] = "RemoteForward", ["serveraliveinterval"] = "ServerAliveInterval" };
        var lines = new List<string>();
        if (h.Group is not null)
        {
            lines.Add($"# group: {h.Group}");
        }

        if (h.Tags.Length > 0)
        {
            lines.Add($"# tags: {string.Join(", ", h.Tags)}");
        }

        if (h.Description is not null)
        {
            lines.Add($"# description: {h.Description}");
        }

        lines.Add($"Host {h.Alias}");
        lines.AddRange(h.Options.Where(o => o.Key != "identityfile").Select(o => $"    {names.GetValueOrDefault(o.Key, o.Key)} {o.Value}"));
        return string.Join("\n", lines);
    }

    public static string? Read(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^#+\s*(?:-{3,}|={3,})\s*(.+?)\s*(?:-{3,}|={3,})\s*$")]
    private static partial Regex Folder();

    [GeneratedRegex(@"^#+\s*(section|group|tags|description|desc|web)\s*:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Meta();

    [GeneratedRegex(@"^(\S+?)\s*(?:=\s*|\s+)(.*)$")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"^\s*(Host\s+\*\s*$|Match\s+all\b)", RegexOptions.IgnoreCase)]
    private static partial Regex CatchAll();

    [GeneratedRegex(@"^\s*(ProxyCommand|LocalCommand|PermitLocalCommand|KnownHostsCommand|Include|IdentityAgent|PKCS11Provider|SecurityKeyProvider)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Risky();

    [GeneratedRegex(@"^\s*Match\b.*\bexec\b", RegexOptions.IgnoreCase)]
    private static partial Regex RiskyMatch();
}
