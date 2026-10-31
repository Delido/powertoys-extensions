using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.Ssh;

public sealed class Source
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>"onedev", "http" or "file".</summary>
    public string Type { get; set; } = "onedev";

    public bool Enabled { get; set; } = true;

    public string? ServerUrl { get; set; }

    public string? Project { get; set; }

    public string? Branch { get; set; }

    /// <summary>File or folder inside the repository / on the share.</summary>
    public string? Path { get; set; }

    /// <summary>OneDev token, DPAPI-protected.</summary>
    public string? Token { get; set; }

    public string? Url { get; set; }

    public string? HeaderName { get; set; }

    /// <summary>HTTP header value, DPAPI-protected.</summary>
    public string? HeaderValue { get; set; }
}

public sealed class SourceState
{
    public DateTimeOffset? LastCheck { get; set; }

    public DateTimeOffset? LastSuccess { get; set; }

    public DateTimeOffset? LastChange { get; set; }

    /// <summary>Commit hash, ETag/Last-Modified or mtime hash of the last downloaded version.</summary>
    public string? Version { get; set; }

    public int Hosts { get; set; }

    public int Blocked { get; set; }

    public string? Error { get; set; }
}

/// <summary>Team host lists that are synced into ~/.ssh/config.d (only downloaded when they changed).</summary>
public sealed class SshSources(Func<bool> blockRisky)
{
    private readonly string _sourcesFile = System.IO.Path.Combine(Paths.For("ssh"), "sources.json");
    private readonly string _statesFile = System.IO.Path.Combine(Paths.For("ssh"), "source-state.json");
    private readonly Lock _gate = new();

    public static string Protect(string value) => value.Length == 0 ? value :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }

    public static string CacheFile(string id) => System.IO.Path.Combine(SshConfig.ConfD, $"{SshConfig.TeamPrefix}{id}.conf");

    public static string NewId(string name)
    {
        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return $"{(slug.Length > 0 ? slug[..Math.Min(30, slug.Length)] : "source")}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";
    }

    public List<Source> Sources() => Read<List<Source>>(_sourcesFile) ?? [];

    public Dictionary<string, SourceState> States() => Read<Dictionary<string, SourceState>>(_statesFile) ?? [];

    public void Save(Source source)
    {
        lock (_gate)
        {
            var list = Sources();
            var index = list.FindIndex(s => s.Id == source.Id);
            if (index >= 0)
            {
                list[index] = source;
            }
            else
            {
                list.Add(source);
            }

            Write(_sourcesFile, list);
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            Write(_sourcesFile, Sources().Where(s => s.Id != id).ToList());
            var states = States();
            states.Remove(id);
            Write(_statesFile, states);
            File.Delete(CacheFile(id));
        }
    }

    /// <summary>True when auto sync is on and the last check is older than the interval.</summary>
    public bool SyncDue(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return false;
        }

        var states = States();
        return Sources().Any(s => s.Enabled && DateTimeOffset.Now - (states.GetValueOrDefault(s.Id)?.LastCheck ?? DateTimeOffset.MinValue) > interval);
    }

    public async Task<List<(Source Source, string Status, SourceState State)>> SyncAll(bool force = false) =>
        [.. await Task.WhenAll(Sources().Select(s => Sync(s, force)))];

    /// <summary>The cache file is only replaced after a successful download, so the last good version keeps working.</summary>
    public async Task<(Source Source, string Status, SourceState State)> Sync(Source source, bool force = false)
    {
        var previous = States().GetValueOrDefault(source.Id) ?? new SourceState();
        if (!source.Enabled)
        {
            return (source, "disabled", previous);
        }

        var now = DateTimeOffset.Now;
        try
        {
            var known = force ? null : previous.Version;
            var (unchanged, version, files) = source.Type switch
            {
                "onedev" => await OneDev(source, known),
                "http" => await HttpFetch(source, known),
                _ => FileFetch(source, known),
            };
            if (unchanged && File.Exists(CacheFile(source.Id)))
            {
                previous.LastCheck = previous.LastSuccess = now;
                previous.Error = null;
                SetState(source.Id, previous);
                return (source, "unchanged", previous);
            }

            if (unchanged)
            {
                return await Sync(source, force: true); // cache file missing → download again
            }

            var blocked = 0;
            var parts = files.Select(f =>
            {
                var (text, b) = blockRisky() ? SshConfig.Sanitize(f.Text) : (f.Text, 0);
                blocked += b;

                // "Match all" keeps one file's trailing Host block from swallowing the next file
                return $"# ---- {f.Name} ----\nMatch all\n{text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd()}\n";
            });
            var content = $"# Managed by the SSH extension – source \"{source.Name}\" ({source.Type})\n# Synced {now:o} – version {version ?? "-"}\n# Do not edit: this file is replaced on the next sync. Override hosts in ~/.ssh/config instead.\n\n" + string.Join("\n", parts);
            Directory.CreateDirectory(SshConfig.ConfD);
            var tmp = CacheFile(source.Id) + ".tmp";
            await File.WriteAllTextAsync(tmp, content);
            File.Move(tmp, CacheFile(source.Id), overwrite: true);
            var state = new SourceState { LastCheck = now, LastSuccess = now, LastChange = now, Version = version, Hosts = Regex.Matches(content, @"^\s*Host\s+", RegexOptions.Multiline | RegexOptions.IgnoreCase).Count, Blocked = blocked };
            SetState(source.Id, state);
            return (source, "updated", state);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            previous.LastCheck = now;
            previous.Error = ex.Message;
            SetState(source.Id, previous);
            return (source, "error", previous);
        }
    }

    private static bool IsConf(string name) => Regex.IsMatch(name, @"\.(conf|config|ssh)$", RegexOptions.IgnoreCase) || name == "config";

    private static async Task<(bool Unchanged, string? Version, List<(string Name, string Text)> Files)> OneDev(Source source, string? known)
    {
        var baseUrl = Http.TrimUrl(source.ServerUrl ?? string.Empty);
        var auth = new AuthenticationHeaderValue("Bearer", Unprotect(source.Token));
        Task<T> Get<T>(string path) => Http.GetJson<T>(Http.Client(), Http.Request(HttpMethod.Get, $"{baseUrl}/~api/{path}", auth));

        var projectPath = (source.Project ?? string.Empty).Trim('/', ' ');
        var projects = await Get<JsonArray>($"projects?query={Uri.EscapeDataString($"\"Path\" is \"{projectPath}\"")}&offset=0&count=1");
        var projectId = projects.FirstOrDefault()?["id"]?.GetValue<int>() ?? throw new InvalidOperationException($"OneDev project \"{projectPath}\" not found or no access");
        var branch = string.IsNullOrWhiteSpace(source.Branch) ? "main" : source.Branch.Trim();

        // cheap change check: only download when the branch head moved
        var head = (await Get<JsonObject>($"repositories/{projectId}/branches/{Uri.EscapeDataString(branch)}"))["commitHash"]!.GetValue<string>();
        if (known is not null && head == known)
        {
            return (true, head, []);
        }

        var target = (source.Path ?? string.Empty).Trim('/', ' ');
        async Task<string> ReadFile(string file) =>
            Encoding.UTF8.GetString(Convert.FromBase64String((await Get<JsonObject>($"repositories/{projectId}/files?revision={Uri.EscapeDataString(head)}&file={Uri.EscapeDataString(file)}"))["base64Content"]!.GetValue<string>()));

        JsonArray? entries = null;
        try
        {
            entries = await Get<JsonArray>($"repositories/{projectId}/directories/{head}/{string.Join("/", target.Split('/').Select(Uri.EscapeDataString))}");
        }
        catch (Exception ex) when (ex is ApiException or JsonException or InvalidOperationException)
        {
            // a file, not a folder
        }

        // a folder (or the repo root) collects all *.conf files; otherwise a single file
        if (entries is not null)
        {
            var files = entries.Where(e => e?["isFile"]?.GetValue<bool>() == true && IsConf(System.IO.Path.GetFileName(e["path"]!.GetValue<string>())))
                .Select(e => e!["path"]!.GetValue<string>()).Order().ToList();
            if (files.Count == 0)
            {
                throw new InvalidOperationException($"No *.conf files in \"{(target.Length > 0 ? target : "/")}\"");
            }

            var result = new List<(string, string)>();
            foreach (var f in files)
            {
                var full = f.Contains('/', StringComparison.Ordinal) || target.Length == 0 ? f : $"{target}/{f}";
                result.Add((full, await ReadFile(full)));
            }

            return (false, head, result);
        }

        return (false, head, [(target, await ReadFile(target))]);
    }

    private static async Task<(bool Unchanged, string? Version, List<(string Name, string Text)> Files)> HttpFetch(Source source, string? known)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, (source.Url ?? string.Empty).Trim());
        if (!string.IsNullOrWhiteSpace(source.HeaderName) && Unprotect(source.HeaderValue) is { Length: > 0 } value)
        {
            request.Headers.TryAddWithoutValidation(source.HeaderName.Trim(), value);
        }

        if (known?.StartsWith("etag:", StringComparison.Ordinal) == true)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", known[5..]);
        }

        if (known?.StartsWith("lm:", StringComparison.Ordinal) == true)
        {
            request.Headers.TryAddWithoutValidation("If-Modified-Since", known[3..]);
        }

        using var response = await Http.Client().SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return (true, known, []);
        }

        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {text[..Math.Min(200, text.Length)]}");
        }

        if (Regex.IsMatch(text, @"^\s*<!doctype html|^\s*<html", RegexOptions.IgnoreCase))
        {
            throw new InvalidOperationException("Got an HTML page instead of an SSH config (login page? wrong URL?)");
        }

        var version = response.Headers.ETag is { } etag ? $"etag:{etag}"
            : response.Content.Headers.LastModified is { } lm ? $"lm:{lm:R}"
            : $"sha:{Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))}";
        return version == known ? (true, version, []) : (false, version, [(source.Url ?? string.Empty, text)]);
    }

    private static (bool Unchanged, string? Version, List<(string Name, string Text)> Files) FileFetch(Source source, string? known)
    {
        var target = (source.Path ?? string.Empty).Trim();
        var files = Directory.Exists(target)
            ? Directory.EnumerateFiles(target).Where(f => IsConf(System.IO.Path.GetFileName(f))).Order().ToList()
            : [target];
        if (files.Count == 0)
        {
            throw new InvalidOperationException($"No *.conf files in {target}");
        }

        var stamps = string.Join("|", files.Select(f => $"{f}:{File.GetLastWriteTimeUtc(f).Ticks}"));
        var version = $"mtime:{Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(stamps)))}";
        return version == known ? (true, version, []) : (false, version, [.. files.Select(f => (f, File.ReadAllText(f)))]);
    }

    private void SetState(string id, SourceState state)
    {
        lock (_gate)
        {
            var states = States();
            states[id] = state;
            Write(_statesFile, states);
        }
    }

    private static T? Read<T>(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file)) : default;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return default;
        }
    }

    private static void Write<T>(string file, T value) => File.WriteAllText(file, JsonSerializer.Serialize(value));
}
