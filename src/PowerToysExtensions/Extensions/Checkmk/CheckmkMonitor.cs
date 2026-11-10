using System.Text.Json;
using System.Text.RegularExpressions;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.Checkmk;

/// <summary>
/// Checks Checkmk in the background and shows a Windows notification for new problems (and optionally
/// recoveries). Runs inside the extension process for as long as Command Palette runs.
/// </summary>
internal sealed class CheckmkMonitor(CheckmkApi api) : IDisposable
{
    private static readonly Dictionary<int, string> ServiceLabels = new() { [1] = "WARN", [2] = "CRIT", [3] = "UNKNOWN" };
    private static readonly Dictionary<int, int> ServiceSeverity = new() { [1] = 1, [3] = 2, [2] = 3 };
    private static readonly Dictionary<int, string> HostLabels = new() { [1] = "DOWN", [2] = "UNREACHABLE" };

    private readonly string _stateFile = Path.Combine(Paths.For("checkmk"), "known-problems.json");
    private readonly SemaphoreSlim _running = new(1, 1);
    private Timer? _timer;
    private string? _lastError;

    public event Action<string>? StatusChanged;

    /// <summary>Starts, stops or re-times the monitor according to the settings.</summary>
    public void Apply()
    {
        _timer?.Dispose();
        _timer = null;
        if (!api.Settings.MonitorEnabled || !api.Settings.Configured)
        {
            StatusChanged?.Invoke(api.Settings.Configured ? "Off" : "Not set up");
            return;
        }

        StatusChanged?.Invoke($"On · every {api.Settings.CheckEvery.TotalMinutes:0} min");
        _timer = new Timer(_ => _ = Check(manual: false), null, TimeSpan.FromSeconds(10), api.Settings.CheckEvery);
    }

    public void CheckNow() => _ = Check(manual: true);

    public void Dispose() => _timer?.Dispose();

    private async Task Check(bool manual)
    {
        if (!await _running.WaitAsync(0))
        {
            return;
        }

        try
        {
            Dictionary<string, Problem> current;
            try
            {
                current = await Collect();
            }
            catch (Exception ex)
            {
                if (manual)
                {
                    Status.Error($"Checkmk not reachable: {ex.Message}");
                }
                else if (_lastError is null)
                {
                    Notify.Show("Checkmk not reachable", ex.Message, group: "checkmk");
                }

                _lastError = ex.Message;
                StatusChanged?.Invoke($"Error: {ex.Message}");
                return;
            }

            if (_lastError is not null)
            {
                _lastError = null;
                if (!manual)
                {
                    Notify.Show("Checkmk reachable again", "Monitoring resumed", group: "checkmk");
                }
            }

            var known = ReadKnown();
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(current));
            var total = current.Count;

            // Global switch in Checkmk's Master control: stay quiet like Checkmk. What comes up meanwhile is
            // remembered as known, so switching notifications on again does not bring a burst of old problems.
            if (api.Settings.RespectNotifications && await api.GlobalNotificationsEnabled() == false)
            {
                StatusChanged?.Invoke($"Quiet – notifications are off in Checkmk · {total} problem{(total == 1 ? string.Empty : "s")} · checked {DateTime.Now:HH:mm}");
                if (manual)
                {
                    Status.Info("Checkmk: notifications are switched off (Master control)");
                }

                return;
            }

            StatusChanged?.Invoke($"{(api.Settings.MonitorEnabled ? "On" : "Off")} · {total} problem{(total == 1 ? string.Empty : "s")} · checked {DateTime.Now:HH:mm}");

            if (known is null)
            {
                // First run: one summary instead of a notification storm
                if (total > 0)
                {
                    Notify.Show("Checkmk", $"{total} current problem{(total == 1 ? string.Empty : "s")}", api.ProblemsUrl, "checkmk");
                }
                else if (manual)
                {
                    Status.Info("Checkmk: no problems");
                }

                return;
            }

            var fresh = current
                .Where(p => !known.TryGetValue(p.Key, out var old) || p.Value.Severity > old.Severity)
                .Select(p => p.Value)
                .OrderByDescending(p => p.Severity)
                .ToList();
            var recovered = known.Where(p => !current.ContainsKey(p.Key)).Select(p => p.Value).ToList();

            if (fresh.Count is > 0 and <= 3)
            {
                foreach (var p in fresh)
                {
                    Notify.Show($"{p.State}: {p.Label}", p.Output.Length > 0 ? p.Output : p.State, p.Url, "checkmk");
                }
            }
            else if (fresh.Count > 3)
            {
                var lines = fresh.Take(4).Select(p => $"{p.State} {p.Label}").ToList();
                if (fresh.Count > 4)
                {
                    lines.Add($"…and {fresh.Count - 4} more");
                }

                Notify.Show($"Checkmk: {fresh.Count} new problems", string.Join("\n", lines), api.ProblemsUrl, "checkmk");
            }

            if (api.Settings.NotifyRecovery && recovered.Count > 0)
            {
                var body = recovered.Count <= 3 ? string.Join("\n", recovered.Select(p => p.Label)) : $"{recovered.Count} problems are OK again";
                Notify.Show("Checkmk: recovered", body, api.ProblemsUrl, "checkmk");
            }

            if (manual && fresh.Count == 0)
            {
                Status.Info(total == 0 ? "Checkmk: no problems" : $"Checkmk: {total} problem{(total == 1 ? string.Empty : "s")}, nothing new");
            }
        }
        finally
        {
            _running.Release();
        }
    }

    private Dictionary<string, Problem>? ReadKnown()
    {
        try
        {
            return File.Exists(_stateFile) ? JsonSerializer.Deserialize<Dictionary<string, Problem>>(File.ReadAllText(_stateFile)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private async Task<Dictionary<string, Problem>> Collect()
    {
        var s = api.Settings;
        var hostRe = Pattern(s.HostFilter);
        var serviceRe = Pattern(s.ServiceFilter);
        var excludeRe = Pattern(s.ServiceExclude);
        string[] states = s.MinSeverity switch
        {
            "warn" => ["1", "2", "3"],
            "crit-unknown" => ["2", "3"],
            _ => ["2"],
        };

        var hostsTask = s.IncludeHosts ? api.Hosts(Q.Ne("state", "0")) : Task.FromResult(Array.Empty<Host>());
        var servicesTask = api.Services(Q.Or([.. states.Select(st => Q.Eq("state", st))]));
        var problems = new Dictionary<string, Problem>();
        var down = new HashSet<string>();

        // notifications switched off in Checkmk for the host / service (for a service also its host)
        bool Ignored(int acknowledged, bool inDowntime, bool soft, bool notificationsOff = false) =>
            (s.RespectNotifications && notificationsOff) ||
            (s.IgnoreAcknowledged && acknowledged > 0) || (s.IgnoreDowntimes && inDowntime) || (s.HardOnly && soft);

        foreach (var h in await hostsTask)
        {
            // services of every host that is down are noise – also when the host itself is not notified
            if (h.HasBeenChecked > 0)
            {
                down.Add(h.Name);
            }

            if (h.HasBeenChecked == 0 || hostRe?.IsMatch(h.Name) == false || Ignored(h.Acknowledged, h.InDowntime, h.Soft, h.NotificationsEnabled == 0))
            {
                continue;
            }

            problems[$"host:{h.Name}"] = new(h.Name, HostLabels.GetValueOrDefault(h.State, "DOWN"), h.PluginOutput, api.HostUrl(h.Name), 10);
        }

        foreach (var sv in await servicesTask)
        {
            if (sv.HasBeenChecked == 0 || hostRe?.IsMatch(sv.HostName) == false || serviceRe?.IsMatch(sv.Description) == false ||
                excludeRe?.IsMatch(sv.Description) == true || Ignored(sv.Acknowledged, sv.InDowntime, sv.Soft, sv.NotificationsEnabled == 0 || sv.HostNotificationsEnabled == 0) || down.Contains(sv.HostName))
            {
                continue;
            }

            problems[$"svc:{sv.HostName}|{sv.Description}"] = new(
                $"{sv.HostName} – {sv.Description}",
                ServiceLabels.GetValueOrDefault(sv.State, sv.State.ToString()),
                sv.PluginOutput,
                api.ServiceUrl(sv.HostName, sv.Description),
                ServiceSeverity.GetValueOrDefault(sv.State));
        }

        return problems;
    }

    private static Regex? Pattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        try
        {
            return new Regex(pattern.Trim(), RegexOptions.IgnoreCase);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"Invalid regular expression: {pattern}");
        }
    }

    internal sealed record Problem(string Label, string State, string Output, string Url, int Severity);
}
