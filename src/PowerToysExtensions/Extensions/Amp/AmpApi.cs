using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.Amp;

public sealed class AmpSettings : SettingsBase
{
    private readonly TextSetting _url = new("serverUrl", "Panel URL", "URL of your AMP panel (ADS)", string.Empty) { Placeholder = "http://192.168.1.10:8080" };
    private readonly TextSetting _user = new("username", "Username", "AMP login name", string.Empty);
    private readonly SecretSetting _password = new("password", "Password", "AMP password (stored encrypted)");

    public AmpSettings()
        : base("amp")
    {
        Settings.Add(_url);
        Settings.Add(_user);
        Settings.Add(_password);
        Load();
    }

    public string ServerUrl => Http.TrimUrl(_url.Value ?? string.Empty);

    public string Username => _user.Value?.Trim() ?? string.Empty;

    public string Password => _password.Value ?? string.Empty;

    public bool Configured => ServerUrl.Length > 0 && Username.Length > 0 && _password.HasValue;
}

/// <summary>AMP's JSON field names differ between versions; read them by any known name, case-insensitively.</summary>
public static class J
{
    public static JsonNode? F(JsonNode? node, params string[] names)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (obj.TryGetPropertyValue(name, out var value) && value is not null)
            {
                return value;
            }
        }

        foreach (var name in names)
        {
            var match = obj.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match.Value is not null)
            {
                return match.Value;
            }
        }

        return null;
    }

    public static string S(JsonNode? node, params string[] names) => F(node, names) switch
    {
        null => string.Empty,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        var other => other.ToJsonString().Trim('"'),
    };

    public static double? N(JsonNode? node, params string[] names) =>
        double.TryParse(S(node, names), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    public static bool B(JsonNode? node, params string[] names) => S(node, names).Equals("true", StringComparison.OrdinalIgnoreCase);

    public static JsonNode[] A(JsonNode? node) => node is JsonArray a ? [.. a.Where(n => n is not null).Select(n => n!)] : [];

    /// <summary>AMP serialises dates as "/Date(1697040000000)/" or ISO strings.</summary>
    public static DateTimeOffset? Date(string value)
    {
        var m = Regex.Match(value, @"/Date\((-?\d+)");
        if (m.Success && long.TryParse(m.Groups[1].Value, out var ms))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        if (long.TryParse(value, out var raw))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(raw);
        }

        return DateTimeOffset.TryParse(value, out var d) ? d : null;
    }
}

public sealed record Metric(double RawValue, double MaxValue, double Percent);

public sealed record Endpoint(string DisplayName, string Address);

public sealed record Instance(
    string InstanceID,
    string InstanceName,
    string FriendlyName,
    string Module,
    string? ModuleDisplayName,
    string? Description,
    bool Running,
    int AppState,
    bool Suspended,
    Dictionary<string, Metric> Metrics,
    Endpoint[] Endpoints,
    string TargetID,
    bool StartOnBoot)
{
    public bool AppReady => Running && AppState == AmpApi.Ready;

    public bool AppStopped => Running && AppState is AmpApi.Stopped or AmpApi.Failed;
}

public sealed record Player(string Id, string Name);

public sealed record Backup(string Id, string Name, string Description, DateTimeOffset? Date, double? Size, bool Sticky, bool Automatic, bool S3);

public sealed record AmpSetting(string Node, string Name, string Description, string Category, string Subcategory, string InputType, string Value, (string Value, string Title)[] Options, bool ReadOnly, string Placeholder, string Suffix);

public sealed record Trigger(string Id, string Description, bool Enabled, string[] Tasks);

public sealed record AppSummary(string Id, string Name, string Module, string Description, string? Image, string[] Tags, string Author, string Deprecated);

public sealed record ConsoleEntry(DateTimeOffset? Time, string Source, string Message, string Type);

public sealed record ServerValue(string Label, string Value);

public sealed class AmpApi(AmpSettings settings)
{
    public const string Ads = "ads";
    public const int Stopped = 0;
    public const int Ready = 20;
    public const int Failed = 100;

    private static readonly Dictionary<string, int> StateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["undefined"] = -1, ["stopped"] = 0, ["prestart"] = 5, ["configuringenvironment"] = 7, ["starting"] = 10, ["ready"] = 20,
        ["restarting"] = 30, ["stopping"] = 40, ["preparingforsleep"] = 45, ["sleeping"] = 50, ["waiting"] = 60, ["installing"] = 70,
        ["updating"] = 75, ["awaitinguserinput"] = 80, ["failed"] = 100, ["suspended"] = 200, ["maintainence"] = 250, ["maintenance"] = 250,
        ["indeterminate"] = 999,
    };

    // Settings that belong to AMP itself (Steam login, mail, backups …), not to the game server
    private static readonly Regex InternalSetting = new(@"steam|smtp|email|mail|login|ldap|oidc|webhook|backup|s3|twofactor|ads\b|remote", RegexOptions.IgnoreCase);
    private static readonly Regex InterestingSetting = new(@"server ?name|player|slot|max ?users|world|map|level|game ?mode|difficulty|motd|description", RegexOptions.IgnoreCase);

    private readonly ConcurrentDictionary<string, string> _sessions = new();

    public AmpSettings Settings => settings;

    public string PanelUrl(string? instanceId = null) => instanceId is null ? settings.ServerUrl : $"{settings.ServerUrl}/?instance={instanceId}";

    /// <summary>Calls an AMP API method, logging in (again) when the session is missing or expired.</summary>
    public async Task<JsonNode?> Call(string scope, string method, JsonObject? parameters = null, CancellationToken token = default)
    {
        if (!settings.Configured)
        {
            throw new InvalidOperationException("AMP is not set up yet – open the settings of this extension");
        }

        try
        {
            return await Raw(scope, method, parameters, _sessions.TryGetValue(scope, out var s) ? s : await Login(scope, token), token);
        }
        catch (UnauthorizedAccessException)
        {
            return await Raw(scope, method, parameters, await Login(scope, token), token);
        }
    }

    /// <summary>Calls a method that returns an ActionResult and throws when AMP reports a failure.</summary>
    public async Task Action(string scope, string method, JsonObject? parameters = null)
    {
        var result = await Call(scope, method, parameters);
        if (J.F(result, "Status") is JsonValue v && v.TryGetValue<bool>(out var ok) && !ok)
        {
            throw new ApiException(J.S(result, "Reason") is { Length: > 0 } r ? r : $"{method} failed");
        }
    }

    public async Task<Instance[]> Instances(CancellationToken token = default)
    {
        var targets = J.A(await Call(Ads, "ADSModule/GetInstances", new JsonObject { ["ForceIncludeSelf"] = false }, token));
        var instances = targets
            .SelectMany(t => J.A(J.F(t, "AvailableInstances", "Instances")).Select(raw => Normalize(raw, J.S(t, "InstanceId", "InstanceID", "Id"))))
            .Where(i => i.InstanceID.Length > 0 && i.Module != "ADS");
        var live = await Task.WhenAll(instances.Select(i => WithLiveState(i, token)));
        return [.. live.OrderBy(i => i.FriendlyName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Starting an ADS instance only launches AMP inside it; this also starts the game server.</summary>
    public async Task StartInstanceAndServer(Instance instance)
    {
        await Action(Ads, "ADSModule/StartInstance", new JsonObject { ["InstanceName"] = instance.InstanceName });
        var deadline = DateTime.UtcNow.AddMinutes(2);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(3000);
            var current = (await Instances()).FirstOrDefault(i => i.InstanceID == instance.InstanceID);
            if (current?.Running != true)
            {
                continue;
            }

            try
            {
                // the instance may accept connections a few seconds after it reports "running"
                await Action(instance.InstanceID, "Core/Start");
                return;
            }
            catch (Exception ex) when (ex is ApiException or UnauthorizedAccessException)
            {
                last = ex;
            }
        }

        throw last ?? new ApiException("Instance did not come online in time");
    }

    public async Task<ConsoleEntry[]> Console(string instanceId, CancellationToken token)
    {
        var updates = await Call(instanceId, "Core/GetUpdates", null, token);
        return [.. J.A(J.F(updates, "ConsoleEntries", "Console", "Entries"))
            .Select(e => new ConsoleEntry(J.Date(J.S(e, "Timestamp", "Time", "Date")), J.S(e, "Source", "Sender", "Origin"), J.S(e, "Message", "Contents", "Content", "Text", "Line"), J.S(e, "Type", "Kind", "Level")))
            .Where(e => e.Message.Length > 0)];
    }

    public Task SendConsole(string instanceId, string message) => Action(instanceId, "Core/SendConsoleMessage", new JsonObject { ["message"] = message });

    public async Task<Player[]> Players(string instanceId, CancellationToken token)
    {
        var users = await Call(instanceId, "Core/GetUserList", null, token);
        return users switch
        {
            JsonArray a => [.. a.Select(u => new Player(J.S(u, "Id", "ID", "UID"), J.S(u, "Name", "Username")))],
            JsonObject o => [.. o.Select(p => new Player(p.Key, p.Value?.ToString() ?? p.Key)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)],
            _ => [],
        };
    }

    public async Task<Backup[]> Backups(string instanceId, CancellationToken token) =>
        [.. J.A(await Call(instanceId, "LocalFileBackupPlugin/GetBackups", null, token))
            .Select(b => new Backup(
                J.S(b, "Id", "ID", "BackupId"),
                J.S(b, "Name", "Title") is { Length: > 0 } n ? n : "Backup",
                J.S(b, "Description"),
                J.Date(J.S(b, "ModifiedDate", "CreatedDate", "StoredDate", "Timestamp", "Date")),
                J.N(b, "TotalSize", "TotalSizeBytes", "Size", "SizeBytes"),
                J.B(b, "Sticky", "IsSticky"),
                J.B(b, "CreatedAutomatically", "WasCreatedAutomatically"),
                J.B(b, "StoredInS3", "S3")))
            .Where(b => b.Id.Length > 0)
            .OrderByDescending(b => b.Date)];

    public Task TakeBackup(string id, string title, string description, bool sticky) =>
        Action(id, "LocalFileBackupPlugin/TakeBackup", new JsonObject { ["Title"] = title, ["Description"] = description, ["Sticky"] = sticky });

    public Task RestoreBackup(string id, string backupId) =>
        Action(id, "LocalFileBackupPlugin/RestoreBackup", new JsonObject { ["BackupId"] = backupId, ["DeleteExistingData"] = false });

    public Task DeleteBackup(string id, string backupId) => Action(id, "LocalFileBackupPlugin/DeleteLocalBackup", new JsonObject { ["BackupId"] = backupId });

    public Task SetBackupSticky(string id, string backupId, bool sticky) =>
        Action(id, "LocalFileBackupPlugin/SetBackupSticky", new JsonObject { ["BackupId"] = backupId, ["Sticky"] = sticky });

    /// <summary>All visible settings of an instance, in AMP's own category order.</summary>
    public async Task<AmpSetting[]> SettingsOf(string instanceId, CancellationToken token)
    {
        if (await Call(instanceId, "Core/GetSettingsSpec", null, token) is not JsonObject spec)
        {
            return [];
        }

        var list = new List<AmpSetting>();
        foreach (var (group, items) in spec)
        {
            foreach (var s in J.A(items))
            {
                var type = J.S(s, "InputType", "Type").ToLowerInvariant();
                if (J.B(s, "Hidden", "IsHidden") || type == "hidden")
                {
                    continue;
                }

                var parts = (J.S(s, "Category") is { Length: > 0 } c ? c : group).Split(':');
                var options = J.F(s, "EnumValues", "Options") switch
                {
                    JsonArray a => a.Select(v => (v?.ToString() ?? string.Empty, v?.ToString() ?? string.Empty)).ToArray(),
                    JsonObject o => o.Select(p => (p.Key, p.Value?.ToString() ?? p.Key)).ToArray(),
                    _ => [],
                };
                list.Add(new AmpSetting(
                    J.S(s, "Node"),
                    J.S(s, "Name", "DisplayName"),
                    J.S(s, "Description"),
                    parts[0].Length > 0 ? parts[0] : group,
                    J.S(s, "Subcategory") is { Length: > 0 } sub ? sub : parts.ElementAtOrDefault(1) ?? string.Empty,
                    type,
                    Text(J.F(s, "CurrentValue", "Value")),
                    options,
                    J.B(s, "ReadOnly", "IsReadOnly"),
                    J.S(s, "Placeholder"),
                    J.S(s, "Suffix")));
            }
        }

        return [.. list.Where(s => s.Node.Length > 0 && s.Name.Length > 0)];
    }

    public Task SetSetting(string instanceId, string node, string value) => Action(instanceId, "Core/SetConfig", new JsonObject { ["node"] = node, ["value"] = value });

    /// <summary>Game server passwords and a few key settings of a running instance.</summary>
    public async Task<(ServerValue[] Passwords, ServerValue[] Settings)> ServerInfo(string instanceId, CancellationToken token)
    {
        var all = (await SettingsOf(instanceId, token)).Where(s => !InternalSetting.IsMatch($"{s.Node} {s.Category}")).ToList();
        var passwords = new List<ServerValue>();
        foreach (var s in all)
        {
            if (s.InputType == "password" && s.Value.Length > 0)
            {
                passwords.Add(new(s.Name, s.Value));
            }
            else
            {
                passwords.AddRange(RolePasswords(s.Value, s.Name));
            }
        }

        // Enshrouded keeps role passwords only in its server JSON
        if (passwords.Count == 0)
        {
            foreach (var path in new[] { "2278520/enshrouded_server.json", "enshrouded_server.json" })
            {
                if (await ReadFile(instanceId, path, token) is not { } content)
                {
                    continue;
                }

                try
                {
                    var json = JsonNode.Parse(content);
                    passwords.AddRange(RolePasswords(J.F(json, "userGroups")?.ToJsonString() ?? string.Empty, "Role"));
                    if (J.S(json, "password") is { Length: > 0 } pw)
                    {
                        passwords.Add(new("Server Password", pw));
                    }

                    break;
                }
                catch (JsonException)
                {
                    // not JSON
                }
            }
        }

        var interesting = all.Where(s => s.InputType != "password" && InterestingSetting.IsMatch(s.Name) && s.Value.Length > 0).Take(15).Select(s => new ServerValue(s.Name, s.Value));
        return ([.. passwords], [.. interesting]);
    }

    public async Task<Trigger[]> Schedule(string instanceId, CancellationToken token)
    {
        var data = await Call(instanceId, "Core/GetScheduleData", null, token);
        var methods = J.A(J.F(data, "AvailableMethods")).ToDictionary(m => J.S(m, "Id", "ID"), m => J.S(m, "Name", "Description"));
        return [.. J.A(J.F(data, "PopulatedTriggers", "Triggers")).Select(t => new Trigger(
            J.S(t, "Id", "ID"),
            J.S(t, "Description", "Name") is { Length: > 0 } d ? d : "Trigger",
            J.F(t, "Enabled", "IsEnabled") is null || J.B(t, "Enabled", "IsEnabled"),
            [.. J.A(J.F(t, "Tasks")).Select(task => J.S(task, "Name", "Description") is { Length: > 0 } n ? n : methods.GetValueOrDefault(J.S(task, "MethodID", "MethodId", "Method")) ?? "Task")]))];
    }

    public Task RunTrigger(string id, string triggerId) => Action(id, "Core/RunEventTriggerImmediately", new JsonObject { ["triggerId"] = triggerId });

    public Task SetTriggerEnabled(string id, string triggerId, bool enabled) => Action(id, "Core/SetTriggerEnabled", new JsonObject { ["Id"] = triggerId, ["Enabled"] = enabled });

    public Task Upgrade(Instance i) => Action(Ads, "ADSModule/UpgradeInstance", new JsonObject { ["InstanceName"] = i.InstanceName, ["RestartIfRunning"] = true });

    public Task UpdateInfo(Instance i, string friendlyName, string description, bool startOnBoot) =>
        Action(Ads, "ADSModule/UpdateInstanceInfo", new JsonObject { ["InstanceId"] = i.InstanceID, ["FriendlyName"] = friendlyName, ["Description"] = description, ["StartOnBoot"] = startOnBoot });

    public Task SetSuspended(Instance i, bool suspended) =>
        Action(Ads, "ADSModule/SetInstanceSuspended", new JsonObject { ["InstanceName"] = i.InstanceName, ["Suspended"] = suspended, ["SuspendReason"] = string.Empty });

    public Task Delete(Instance i) => Action(Ads, "ADSModule/DeleteInstance", new JsonObject { ["InstanceName"] = i.InstanceName });

    public async Task StartAll() => await Action(Ads, "ADSModule/StartAllInstances", new JsonObject { ["TargetADSInstance"] = await TargetId() });

    public async Task StopAll() => await Action(Ads, "ADSModule/StopAllInstances", new JsonObject { ["TargetADSInstance"] = await TargetId() });

    public async Task UpgradeAll() => await Action(Ads, "ADSModule/UpgradeAllInstances", new JsonObject { ["TargetADSInstance"] = await TargetId(), ["RestartRunning"] = true });

    public async Task<AppSummary[]> Apps(CancellationToken token) =>
        [.. J.A(await Call(Ads, "ADSModule/GetSupportedAppSummaries", null, token))
            .Where(a => !J.B(a, "IsServiceSpec"))
            .Select(a => new AppSummary(
                J.S(a, "Id", "ID", "SpecId", "AppId"),
                J.S(a, "FriendlyName", "DisplayName", "Name"),
                J.S(a, "Module", "ModuleName"),
                J.S(a, "Description"),
                ImageUrl(J.S(a, "DisplayImageSource", "ImageURL", "Image")),
                [.. J.A(J.F(a, "Tags", "Categories")).Select(t => t.ToString())],
                J.S(a, "Author"),
                J.S(a, "DeprecatedReason")))
            .Where(a => a.Id.Length > 0 && a.Name.Length > 0)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];

    public async Task CreateServer(AppSummary app, string friendlyName, bool startOnBoot) =>
        await Action(Ads, "ADSModule/CreateInstanceFromSpec", new JsonObject { ["SpecId"] = app.Id, ["TargetADSInstance"] = await TargetId(), ["FriendlyName"] = friendlyName, ["StartOnBoot"] = startOnBoot });

    /// <summary>Waits for a newly created instance, starts it and triggers the game server installation.</summary>
    public async Task InstallNewServer(string friendlyName)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        Instance? instance = null;
        while (DateTime.UtcNow < deadline && (instance = (await Instances()).FirstOrDefault(i => i.FriendlyName == friendlyName)) is null)
        {
            await Task.Delay(3000);
        }

        if (instance is null)
        {
            throw new ApiException("The new instance did not appear in time");
        }

        if (!instance.Running)
        {
            await Action(Ads, "ADSModule/StartInstance", new JsonObject { ["InstanceName"] = instance.InstanceName });
        }

        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(3000);
            if ((await Instances()).FirstOrDefault(i => i.InstanceID == instance.InstanceID)?.Running != true)
            {
                continue;
            }

            try
            {
                await Action(instance.InstanceID, "Core/UpdateApplication");
                return;
            }
            catch (Exception ex) when (ex is ApiException or UnauthorizedAccessException)
            {
                last = ex;
            }
        }

        throw last ?? new ApiException("The new instance did not start in time");
    }

    public static int ParseState(JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<int>(out var n) => n,
        JsonValue v when v.TryGetValue<string>(out var s) => int.TryParse(s, out var n) ? n : StateNames.GetValueOrDefault(Regex.Replace(s, @"[\s_]", string.Empty), -1),
        _ => -1,
    };

    private static string Text(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => value.ToJsonString(),
    };

    /// <summary>Per-role passwords from a JSON list like Enshrouded's userGroups ([{ name, password }]).</summary>
    private static IEnumerable<ServerValue> RolePasswords(string value, string source)
    {
        if (!value.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        try
        {
            return J.A(JsonNode.Parse(value))
                .Select(r => new ServerValue(J.S(r, "name", "role") is { Length: > 0 } n ? $"{source}: {n}" : source, J.S(r, "password")))
                .Where(r => r.Value.Length > 0)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ImageUrl(string source)
    {
        var steam = Regex.Match(source, @"^steam:(\d+)$");
        if (steam.Success)
        {
            return $"https://cdn.cloudflare.steamstatic.com/steam/apps/{steam.Groups[1].Value}/header.jpg";
        }

        var url = source.StartsWith("url:", StringComparison.Ordinal) ? source[4..] : source;
        return url.StartsWith("http", StringComparison.Ordinal) ? url : null;
    }

    private static Dictionary<string, Metric> Metrics(JsonNode? raw)
    {
        var metrics = new Dictionary<string, Metric>();
        if (raw is not JsonObject obj)
        {
            return metrics;
        }

        foreach (var (key, value) in obj)
        {
            if (J.N(value, "RawValue", "Value", "Current") is not { } v)
            {
                continue;
            }

            var max = J.N(value, "MaxValue", "Max", "Limit") ?? 0;
            metrics[key] = new Metric(v, max, J.N(value, "Percent", "Percentage") ?? (max > 0 ? Math.Round(v / max * 100) : 0));
        }

        return metrics;
    }

    private static Instance Normalize(JsonNode raw, string targetId)
    {
        var name = J.S(raw, "InstanceName", "Name");
        return new Instance(
            J.S(raw, "InstanceID", "InstanceId", "Id"),
            name,
            J.S(raw, "FriendlyName", "DisplayName") is { Length: > 0 } f ? f : name,
            J.S(raw, "Module", "ModuleName"),
            J.S(raw, "ModuleDisplayName", "ApplicationName") is { Length: > 0 } m ? m : null,
            J.S(raw, "Description") is { Length: > 0 } d ? d : null,
            J.B(raw, "Running", "IsRunning"),
            ParseState(J.F(raw, "AppState", "State")),
            J.B(raw, "Suspended", "IsSuspended"),
            Metrics(J.F(raw, "Metrics")),
            [.. J.A(J.F(raw, "ApplicationEndpoints", "Endpoints"))
                .Select(e => new Endpoint(J.S(e, "DisplayName", "Name") is { Length: > 0 } n ? n : "Address", J.S(e, "Endpoint", "Uri")))
                .Where(e => e.Address.Length > 0)],
            J.S(raw, "TargetID", "TargetId") is { Length: > 0 } t ? t : targetId,
            J.B(raw, "DaemonAutostart", "StartOnBoot"));
    }

    /// <summary>AMP 2.8 no longer reports the application state in GetInstances (always -1): ask running instances.</summary>
    private async Task<Instance> WithLiveState(Instance i, CancellationToken token)
    {
        if (!i.Running || i.AppState != -1)
        {
            return i;
        }

        try
        {
            var status = await Call(i.InstanceID, "Core/GetStatus", null, token);
            var metrics = new Dictionary<string, Metric>(i.Metrics);
            foreach (var (k, v) in Metrics(J.F(status, "Metrics")))
            {
                metrics[k] = v;
            }

            return i with { AppState = ParseState(J.F(status, "State", "AppState")), Metrics = metrics };
        }
        catch (Exception ex) when (ex is ApiException or UnauthorizedAccessException or JsonException)
        {
            return i;
        }
    }

    private async Task<string> TargetId()
    {
        var targets = J.A(await Call(Ads, "ADSModule/GetInstances", new JsonObject { ["ForceIncludeSelf"] = false }));
        return J.S(targets.FirstOrDefault(), "InstanceId", "InstanceID", "Id") is { Length: > 0 } id
            ? id
            : throw new ApiException("Could not determine the AMP controller (target) ID");
    }

    private async Task<string?> ReadFile(string instanceId, string path, CancellationToken token)
    {
        try
        {
            var result = await Call(instanceId, "FileManagerPlugin/ReadFileChunk", new JsonObject { ["Filename"] = path, ["Offset"] = 0, ["ChunkSize"] = 1024 * 1024 }, token);
            var data = result is JsonValue ? result.ToString() : J.S(result, "Result", "Data");
            if (data.Length == 0)
            {
                return null;
            }

            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(data));
                return decoded.TrimStart().StartsWith('{') ? decoded : data;
            }
            catch (FormatException)
            {
                return data;
            }
        }
        catch (Exception ex) when (ex is ApiException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<string> Login(string scope, CancellationToken token)
    {
        var result = await Raw(scope, "Core/Login", new JsonObject { ["username"] = settings.Username, ["password"] = settings.Password, ["token"] = string.Empty, ["rememberMe"] = false }, string.Empty, token);
        var session = J.S(result, "sessionID", "SessionId");
        if (!J.B(result, "success", "Success") || session.Length == 0)
        {
            var reason = J.S(result, "resultReason", "ResultReason");
            throw new ApiException($"AMP login failed{(reason.Length > 0 ? $": {reason}" : " – check username and password")}");
        }

        _sessions[scope] = session;
        return session;
    }

    private async Task<JsonNode?> Raw(string scope, string method, JsonObject? parameters, string session, CancellationToken token)
    {
        var body = parameters?.DeepClone().AsObject() ?? [];
        body["SESSIONID"] = session;
        var baseUrl = scope == Ads ? settings.ServerUrl : $"{settings.ServerUrl}/API/ADSModule/Servers/{scope}";
        var request = Http.Request(HttpMethod.Post, $"{baseUrl}/API/{method}");
        request.Content = JsonContent.Create(body);

        HttpResponseMessage response;
        try
        {
            response = await Http.Send(Http.Client(), request, token);
        }
        catch (ApiException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException(ex.Message);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(token);
            var data = text.Length > 0 ? JsonNode.Parse(text) : null;
            if (data is JsonObject obj)
            {
                // AMP reports errors as { Title, Message, StackTrace } with HTTP 200
                if (J.F(obj, "Title") is JsonValue title && J.F(obj, "StackTrace") is not null)
                {
                    var t = title.ToString();
                    if (Regex.IsMatch(t, "unauthori[sz]ed", RegexOptions.IgnoreCase))
                    {
                        throw new UnauthorizedAccessException(J.S(obj, "Message"));
                    }

                    throw new ApiException($"{t}: {J.S(obj, "Message")}");
                }

                // older AMP versions wrap results in { result: … }
                if (obj.Count == 1 && obj.First().Key.Equals("result", StringComparison.OrdinalIgnoreCase))
                {
                    return obj.First().Value;
                }
            }

            return data;
        }
    }
}
