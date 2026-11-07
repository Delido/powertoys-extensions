using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.Checkmk;

public sealed class CheckmkSettings : SettingsBase
{
    private readonly TextSetting _site = new("siteUrl", "Site URL", "URL of your Checkmk site including the site name", string.Empty) { Placeholder = "https://monitoring.example.com/mysite" };
    private readonly TextSetting _user = new("username", "Username", "Checkmk (automation) user", string.Empty);
    private readonly SecretSetting _secret = new("secret", "Automation Secret / Password", "Automation secret of the user (Setup → Users → Automation secret), stored encrypted");
    private readonly ToggleSetting _insecure = new("allowInsecure", "Skip TLS certificate validation", "Only for internal servers with self-signed certificates", false);

    private readonly ToggleSetting _monitor = new("monitorEnabled", "Problem monitor", "Check Checkmk in the background and show a Windows notification for new problems", false);
    private readonly ChoiceSetSetting _every = new("checkEvery", "Check every", "How often the monitor queries Checkmk", Choices(("1 minute", "1"), ("2 minutes", "2"), ("5 minutes", "5"), ("10 minutes", "10"), ("15 minutes", "15"), ("30 minutes", "30")));
    private readonly ChoiceSetSetting _severity = new("minSeverity", "Minimum severity", "Which service states trigger a notification", Choices(("CRIT only", "crit"), ("CRIT + UNKNOWN", "crit-unknown"), ("WARN, CRIT + UNKNOWN", "warn")));
    private readonly ToggleSetting _hosts = new("includeHosts", "Notify when hosts go DOWN / UNREACHABLE", string.Empty, true);
    private readonly ChoiceSetSetting _stateType = new("stateType", "State type", "SOFT states are still being re-checked and often disappear by themselves; Checkmk itself only notifies on HARD states", Choices(("HARD states only", "hard"), ("HARD and SOFT states", "all")));
    private readonly ToggleSetting _ignoreAcknowledged = new("ignoreAcknowledged", "Ignore acknowledged problems", string.Empty, true);
    private readonly ToggleSetting _ignoreDowntimes = new("ignoreDowntimes", "Ignore problems in a downtime", "A service also counts as in downtime while its host is", true);
    private readonly ToggleSetting _recovery = new("notifyRecovery", "Notify when problems recover", string.Empty, false);
    private readonly TextSetting _hostFilter = new("hostFilter", "Only hosts matching", "Optional regular expression (case-insensitive) for host names", string.Empty);
    private readonly TextSetting _serviceFilter = new("serviceFilter", "Only services matching", "Optional regular expression (case-insensitive) for service names", string.Empty);
    private readonly TextSetting _serviceExclude = new("serviceExclude", "Exclude services matching", "Optional regular expression (case-insensitive) for services to ignore", string.Empty);

    public CheckmkSettings()
        : base("checkmk")
    {
        Settings.Add(_site);
        Settings.Add(_user);
        Settings.Add(_secret);
        Settings.Add(_insecure);
        Settings.Add(_monitor);
        Settings.Add(_every);
        Settings.Add(_severity);
        Settings.Add(_hosts);
        Settings.Add(_stateType);
        Settings.Add(_ignoreAcknowledged);
        Settings.Add(_ignoreDowntimes);
        Settings.Add(_recovery);
        Settings.Add(_hostFilter);
        Settings.Add(_serviceFilter);
        Settings.Add(_serviceExclude);
        Load();
    }

    public string SiteUrl => Regex.Replace(Http.TrimUrl(_site.Value ?? string.Empty), "/check_mk$", string.Empty);

    public bool Configured => SiteUrl.Length > 0 && !string.IsNullOrWhiteSpace(_user.Value) && _secret.HasValue;

    public AuthenticationHeaderValue Auth => new("Bearer", $"{_user.Value?.Trim()} {_secret.Value}");

    public bool AllowInsecure => _insecure.Value;

    public bool MonitorEnabled => _monitor.Value;

    public TimeSpan CheckEvery => TimeSpan.FromMinutes(int.TryParse(_every.Value, out var m) ? m : 1);

    public string MinSeverity => _severity.Value ?? "crit";

    public bool IncludeHosts => _hosts.Value;

    public bool HardOnly => _stateType.Value != "all";

    public bool IgnoreAcknowledged => _ignoreAcknowledged.Value;

    public bool IgnoreDowntimes => _ignoreDowntimes.Value;

    public bool NotifyRecovery => _recovery.Value;

    public string HostFilter => _hostFilter.Value ?? string.Empty;

    public string ServiceFilter => _serviceFilter.Value ?? string.Empty;

    public string ServiceExclude => _serviceExclude.Value ?? string.Empty;
}

public sealed record Host(
    string Name,
    string Alias,
    string Address,
    int State,
    int HasBeenChecked,
    string PluginOutput,
    long LastStateChange,
    int Acknowledged,
    int ScheduledDowntimeDepth,
    int NumServicesOk,
    int NumServicesWarn,
    int NumServicesCrit,
    int NumServicesUnknown,
    int StateType = 1)
{
    public bool InDowntime => ScheduledDowntimeDepth > 0;

    /// <summary>SOFT: still being re-checked, may go away by itself.</summary>
    public bool Soft => State != 0 && StateType == 0;

    public bool Handled => Acknowledged > 0 || InDowntime;
}

public sealed record Service(
    string HostName,
    string Description,
    int State,
    int HasBeenChecked,
    string PluginOutput,
    string LongPluginOutput,
    long LastStateChange,
    long LastCheck,
    int Acknowledged,
    int ScheduledDowntimeDepth,
    int StateType = 1,
    int HostScheduledDowntimeDepth = 0)
{
    /// <summary>A service is in downtime as well while its host is (Checkmk treats it the same way).</summary>
    public bool InDowntime => ScheduledDowntimeDepth > 0 || HostScheduledDowntimeDepth > 0;

    /// <summary>SOFT: still being re-checked, may go away by itself.</summary>
    public bool Soft => State != 0 && StateType == 0;

    public bool Handled => Acknowledged > 0 || InDowntime;
}

/// <summary>Livestatus query expressions as used by the Checkmk REST API.</summary>
public static class Q
{
    public static JsonObject Op(string op, string left, string right) => new() { ["op"] = op, ["left"] = left, ["right"] = right };

    public static JsonObject Eq(string left, string right) => Op("=", left, right);

    public static JsonObject Ne(string left, string right) => Op("!=", left, right);

    public static JsonObject Match(string left, string regex) => Op("~~", left, regex);

    public static JsonObject Or(params JsonNode[] expr) => new() { ["op"] = "or", ["expr"] = new JsonArray(expr) };

    public static JsonObject? And(IReadOnlyList<JsonObject> parts) => parts.Count switch
    {
        0 => null,
        1 => parts[0],
        _ => new JsonObject { ["op"] = "and", ["expr"] = new JsonArray([.. parts]) },
    };

    public static string Escape(string text) => Regex.Escape(text).Replace("]", "\\]", StringComparison.Ordinal);
}

public sealed class CheckmkApi(CheckmkSettings settings)
{
    private static readonly string[] HostColumns = ["name", "alias", "address", "state", "has_been_checked", "plugin_output", "last_state_change", "acknowledged", "scheduled_downtime_depth", "num_services_ok", "num_services_warn", "num_services_crit", "num_services_unknown", "state_type"];
    private static readonly string[] ServiceColumns = ["host_name", "description", "state", "has_been_checked", "plugin_output", "long_plugin_output", "last_state_change", "last_check", "acknowledged", "scheduled_downtime_depth", "state_type", "host_scheduled_downtime_depth"];

    public CheckmkSettings Settings => settings;

    public Task<Host[]> Hosts(JsonObject? query, CancellationToken token = default) => Collection<Host>("host", HostColumns, query, token);

    public Task<Service[]> Services(JsonObject? query, CancellationToken token = default) => Collection<Service>("service", ServiceColumns, query, token);

    public Task Acknowledge(string host, string? service, string comment)
    {
        JsonObject body = service is null
            ? new() { ["acknowledge_type"] = "host", ["host_name"] = host }
            : new() { ["acknowledge_type"] = "service", ["host_name"] = host, ["service_description"] = service };
        body["comment"] = comment;
        body["sticky"] = true;
        body["persistent"] = false;
        body["notify"] = false;
        return Post($"domain-types/acknowledge/collections/{(service is null ? "host" : "service")}", body);
    }

    public Task Downtime(string host, string? service, int minutes, string comment)
    {
        var start = DateTimeOffset.UtcNow;
        JsonObject body = service is null
            ? new() { ["downtime_type"] = "host", ["host_name"] = host }
            : new() { ["downtime_type"] = "service", ["host_name"] = host, ["service_descriptions"] = new JsonArray(service) };
        body["start_time"] = start.ToString("o");
        body["end_time"] = start.AddMinutes(minutes).ToString("o");
        body["comment"] = comment;
        return Post($"domain-types/downtime/collections/{(service is null ? "host" : "service")}", body);
    }

    public string HostUrl(string host) => View($"view.py?view_name=host&host={Uri.EscapeDataString(host)}");

    public string ServiceUrl(string host, string service) =>
        View($"view.py?view_name=service&host={Uri.EscapeDataString(host)}&service={Uri.EscapeDataString(service)}");

    public string ProblemsUrl => View("view.py?view_name=svcproblems");

    private string View(string target) => $"{settings.SiteUrl}/check_mk/index.py?start_url={Uri.EscapeDataString(target)}";

    private async Task<T[]> Collection<T>(string domain, string[] columns, JsonObject? query, CancellationToken token)
    {
        var parts = columns.Select(c => ("columns", (string?)c)).ToList();
        if (query is not null)
        {
            parts.Add(("query", query.ToJsonString()));
        }

        var result = await Http.GetJson<CollectionResult<T>>(Client, Request(HttpMethod.Get, $"domain-types/{domain}/collections/all?{Http.Query([.. parts])}"), token, Http.SnakeJson);
        return [.. result.Value.Select(v => v.Extensions)];
    }

    private async Task Post(string path, JsonObject body)
    {
        var request = Request(HttpMethod.Post, path);
        request.Content = JsonContent.Create(body);
        using var _ = await Http.Send(Client, request);
    }

    private HttpClient Client => Http.Client(settings.AllowInsecure);

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        if (!settings.Configured)
        {
            throw new InvalidOperationException("Checkmk is not set up yet – open the settings of this extension");
        }

        return Http.Request(method, $"{settings.SiteUrl}/check_mk/api/1.0/{path}", settings.Auth);
    }

    private sealed record CollectionResult<T>(Entry<T>[] Value);

    private sealed record Entry<T>(T Extensions);
}
