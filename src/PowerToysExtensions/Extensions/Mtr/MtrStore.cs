using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.Mtr;

public sealed class MtrSettings : SettingsBase
{
    private readonly ToggleSetting _monitor = new("monitorEnabled", "Background monitor", "Check all monitored targets in the background and notify about outages, high loss or latency and route changes", false);
    private readonly ChoiceSetSetting _every = new("checkEvery", "Check every", "How often the monitor checks the targets", Choices(("1 minute", "1"), ("2 minutes", "2"), ("5 minutes", "5"), ("10 minutes", "10"), ("15 minutes", "15"), ("30 minutes", "30")));
    private readonly TextSetting _rounds = new("rounds", "Probes per check", "Probe rounds per background check, one per second (routers often rate-limit faster probing)", "10");
    private readonly TextSetting _lossAlert = new("lossAlert", "Loss alert (%)", "Packet loss to the target at which it counts as degraded", "20");
    private readonly TextSetting _latencyAlert = new("latencyAlert", "Latency alert (ms)", "Average latency to the target at which it counts as degraded", "150");
    private readonly ToggleSetting _route = new("notifyRouteChange", "Notify when the path to a target changes", string.Empty, true);
    private readonly ToggleSetting _recovery = new("notifyRecovery", "Notify when a target is reachable again", string.Empty, true);
    private readonly TextSetting _retention = new("retentionDays", "Keep history (days)", "Older measurements are deleted", "7");
    private readonly TextSetting _interval = new("traceInterval", "Live trace interval (ms)", "Time between probe rounds in “Trace Route”", "1000");
    private readonly TextSetting _timeout = new("timeout", "Timeout (ms)", "How long to wait for each reply", "1000");
    private readonly TextSetting _maxHops = new("maxHops", "Max hops", "Highest TTL that is probed", "30");
    private readonly TextSetting _size = new("packetSize", "Packet size (bytes)", "Payload size of each ICMP echo request", "64");

    public MtrSettings()
        : base("mtr")
    {
        Settings.Add(_monitor);
        Settings.Add(_every);
        Settings.Add(_rounds);
        Settings.Add(_lossAlert);
        Settings.Add(_latencyAlert);
        Settings.Add(_route);
        Settings.Add(_recovery);
        Settings.Add(_retention);
        Settings.Add(_interval);
        Settings.Add(_timeout);
        Settings.Add(_maxHops);
        Settings.Add(_size);
        Load();
    }

    public bool MonitorEnabled => _monitor.Value;

    public TimeSpan CheckEvery => TimeSpan.FromMinutes(Num(_every, 1));

    public int Rounds => (int)Math.Max(3, Num(_rounds, 10));

    public double LossAlert => Num(_lossAlert, 20);

    public double LatencyAlert => Num(_latencyAlert, 150);

    public bool NotifyRouteChange => _route.Value;

    public bool NotifyRecovery => _recovery.Value;

    public TimeSpan Retention => TimeSpan.FromDays(Num(_retention, 7));

    public int TraceInterval => (int)Math.Max(200, Num(_interval, 1000));

    public int Timeout => (int)Num(_timeout, 1000);

    public int MaxHops => (int)Math.Min(64, Num(_maxHops, 30));

    public int Size => (int)Num(_size, 64);

    public MtrProbe Probe(string target) => new(target, Timeout, MaxHops, Size);

    private static double Num(Setting<string> s, double fallback) =>
        double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;
}

public sealed record Target(string Id, string Host, string? Label, DateTimeOffset AddedAt)
{
    public string Name => Label ?? Host;
}

public sealed class TargetState
{
    public string Status { get; set; } = "unknown";

    public string? Route { get; set; }

    /// <summary>A different route seen once; becomes the route if it shows up again.</summary>
    public string? PendingRoute { get; set; }

    public DateTimeOffset? LastCheck { get; set; }

    public Sample? LastSample { get; set; }

    public Dictionary<string, string> Hosts { get; set; } = [];

    public string? Error { get; set; }
}

public sealed record MonitorEvent(DateTimeOffset T, string Target, string Host, string Kind, string Message, string? From = null, string? To = null);

/// <summary>Targets, states, per-target history (JSON lines) and events in the extension's data folder.</summary>
public sealed class MtrStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _dir = Paths.For("mtr");
    private readonly Lock _gate = new();

    public string ReportPath => Path.Combine(_dir, "report.html");

    private string TargetsFile => Path.Combine(_dir, "targets.json");

    private string StatesFile => Path.Combine(_dir, "states.json");

    private string EventsFile => Path.Combine(_dir, "events.jsonl");

    private string HistoryDir => Directory.CreateDirectory(Path.Combine(_dir, "history")).FullName;

    public List<Target> Targets() => Read<List<Target>>(TargetsFile) ?? [];

    public Target AddTarget(string host, string? label)
    {
        lock (_gate)
        {
            var targets = Targets();
            if (targets.FirstOrDefault(t => t.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) is { } existing)
            {
                return existing;
            }

            var target = new Target($"{string.Concat(host.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'))}-{DateTimeOffset.Now.ToUnixTimeSeconds():x}", host, string.IsNullOrWhiteSpace(label) ? null : label.Trim(), DateTimeOffset.Now);
            targets.Add(target);
            Write(TargetsFile, targets);
            return target;
        }
    }

    public void UpdateTarget(string id, string host, string? label)
    {
        lock (_gate)
        {
            Write(TargetsFile, Targets().Select(t => t.Id == id ? t with { Host = host, Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim() } : t).ToList());
        }
    }

    public void RemoveTarget(string id)
    {
        lock (_gate)
        {
            Write(TargetsFile, Targets().Where(t => t.Id != id).ToList());
            var states = States();
            states.Remove(id);
            Write(StatesFile, states);
            File.Delete(HistoryFile(id));
        }
    }

    public Dictionary<string, TargetState> States() => Read<Dictionary<string, TargetState>>(StatesFile) ?? [];

    public TargetState State(string id) => States().GetValueOrDefault(id) ?? new TargetState();

    public void SetState(string id, TargetState state)
    {
        lock (_gate)
        {
            var states = States();
            states[id] = state;
            Write(StatesFile, states);
        }
    }

    public void ResetRoute(string id)
    {
        var state = State(id);
        state.Route = null;
        state.PendingRoute = null;
        SetState(id, state);
    }

    public void AppendSample(string id, Sample sample)
    {
        lock (_gate)
        {
            File.AppendAllText(HistoryFile(id), JsonSerializer.Serialize(sample, Json) + "\n");
        }
    }

    public List<Sample> Samples(string id, DateTimeOffset since) => Lines<Sample>(HistoryFile(id)).Where(s => s.T >= since).ToList();

    public void ClearSamples(string id) => File.Delete(HistoryFile(id));

    public void AppendEvent(MonitorEvent e)
    {
        lock (_gate)
        {
            File.AppendAllText(EventsFile, JsonSerializer.Serialize(e, Json) + "\n");
        }
    }

    public List<MonitorEvent> Events(int limit = 200, string? target = null) =>
        Lines<MonitorEvent>(EventsFile).Where(e => target is null || e.Target == target).TakeLast(limit).Reverse().ToList();

    /// <summary>Removes data older than the retention time.</summary>
    public void Trim(TimeSpan retention)
    {
        var since = DateTimeOffset.Now - retention;
        lock (_gate)
        {
            foreach (var t in Targets())
            {
                var kept = Lines<Sample>(HistoryFile(t.Id)).Where(s => s.T >= since).ToList();
                File.WriteAllLines(HistoryFile(t.Id), kept.Select(s => JsonSerializer.Serialize(s, Json)));
            }

            File.WriteAllLines(EventsFile, Lines<MonitorEvent>(EventsFile).Where(e => e.T >= since).Select(e => JsonSerializer.Serialize(e, Json)));
        }
    }

    private static IEnumerable<T> Lines<T>(string file)
    {
        if (!File.Exists(file))
        {
            yield break;
        }

        foreach (var line in File.ReadAllLines(file))
        {
            T? item = default;
            try
            {
                item = line.Length > 0 ? JsonSerializer.Deserialize<T>(line, Json) : default;
            }
            catch (JsonException)
            {
                // skip a partially written line
            }

            if (item is not null)
            {
                yield return item;
            }
        }
    }

    private static T? Read<T>(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json) : default;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return default;
        }
    }

    private static void Write<T>(string file, T value) => File.WriteAllText(file, JsonSerializer.Serialize(value, Json));

    private string HistoryFile(string id) => Path.Combine(HistoryDir, $"{id}.jsonl");
}

/// <summary>Runs the background checks inside the extension and notifies about changes.</summary>
public sealed class MtrMonitor : IDisposable
{
    private readonly MtrSettings _settings;
    private readonly MtrStore _store;
    private readonly SemaphoreSlim _running = new(1, 1);
    private Timer? _timer;
    private DateTime _lastTrim;

    public MtrMonitor(MtrSettings settings, MtrStore store)
    {
        _settings = settings;
        _store = store;
        settings.Changed += Apply;
        Apply();
    }

    public event Action<string>? StatusChanged;

    public string Status { get; private set; } = string.Empty;

    public void Apply()
    {
        _timer?.Dispose();
        _timer = null;
        if (_settings.MonitorEnabled)
        {
            _timer = new Timer(_ => _ = Check(manual: false), null, TimeSpan.FromSeconds(20), _settings.CheckEvery);
        }

        SetStatus(_settings.MonitorEnabled ? $"Monitor on – every {_settings.CheckEvery.TotalMinutes:0} min" : "Monitor off – switch it on in the settings");
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>Checks all targets once. Returns a short summary.</summary>
    public async Task<string> Check(bool manual)
    {
        if (!await _running.WaitAsync(0))
        {
            return "A check is already running";
        }

        try
        {
            var targets = _store.Targets();
            if (targets.Count == 0)
            {
                return "No targets";
            }

            var states = await Task.WhenAll(targets.Select(async t =>
            {
                var state = await CheckTarget(t);
                _store.SetState(t.Id, state);
                return state;
            }));

            if (DateTime.UtcNow - _lastTrim > TimeSpan.FromHours(1))
            {
                _store.Trim(_settings.Retention);
                _lastTrim = DateTime.UtcNow;
            }

            File.WriteAllText(_store.ReportPath, MtrReport.Build(_store));
            var bad = states.Count(s => s.Status != "ok");
            var summary = bad == 0 ? $"{targets.Count} target{(targets.Count == 1 ? string.Empty : "s")} OK" : $"{bad} of {targets.Count} with problems";
            SetStatus($"{summary} · {DateTime.Now:HH:mm}");
            return summary;
        }
        catch (Exception ex)
        {
            Log.Error("mtr monitor", ex);
            return $"Check failed: {ex.Message}";
        }
        finally
        {
            _running.Release();
        }
    }

    private static string DescribeRouteChange(string from, string to)
    {
        var a = from.Split('>');
        var b = to.Split('>');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : null;
            var y = i < b.Length ? b[i] : null;
            if (x != y && x != "*" && y != "*")
            {
                return $"Hop {i + 1}: {x ?? "–"} → {y ?? "–"} ({a.Length} → {b.Length} hops)";
            }
        }

        return $"{a.Length} → {b.Length} hops";
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    private void Event(Target target, string kind, string message, string? from = null, string? to = null)
    {
        _store.AppendEvent(new MonitorEvent(DateTimeOffset.Now, target.Id, target.Host, kind, message, from, to));
        if ((kind == "route" && !_settings.NotifyRouteChange) || (kind == "recovered" && !_settings.NotifyRecovery))
        {
            return;
        }

        var title = kind switch
        {
            "down" => $"🔴 {target.Name} unreachable",
            "degraded" => $"🟠 {target.Name} degraded",
            "recovered" => $"🟢 {target.Name} OK again",
            "route" => $"🔀 Route to {target.Name} changed",
            _ => $"⚠️ {target.Name}: check failed",
        };
        Notify.Show(title, message, new Uri(_store.ReportPath).AbsoluteUri, "mtr");
    }

    private async Task<TargetState> CheckTarget(Target target)
    {
        var previous = _store.State(target.Id);
        var state = new TargetState { Status = previous.Status, Route = previous.Route, PendingRoute = previous.PendingRoute, Hosts = previous.Hosts, LastSample = previous.LastSample, LastCheck = DateTimeOffset.Now };
        Sample sample;
        try
        {
            var probe = _settings.Probe(target.Host);
            probe.HostResolved += (addr, name) =>
            {
                lock (state.Hosts)
                {
                    state.Hosts[addr] = name;
                }
            };
            var ip = await probe.Resolve(CancellationToken.None);
            var rounds = new List<Round>();
            for (var i = 1; i <= _settings.Rounds; i++)
            {
                var start = DateTime.UtcNow;
                rounds.Add(await probe.Run(i, CancellationToken.None));
                var wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - start);
                if (wait > TimeSpan.Zero && i < _settings.Rounds)
                {
                    await Task.Delay(wait);
                }
            }

            sample = Stats.Summarize(ip.ToString(), rounds);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            state.Error = ex.Message;
            state.Status = "down";
            if (previous.Status != "down")
            {
                Event(target, "error", ex.Message);
            }

            return state;
        }

        _store.AppendSample(target.Id, sample);
        state.LastSample = sample;

        var (status, reason) = !sample.Reached ? ("down", "No reply from the target")
            : sample.Loss >= _settings.LossAlert ? ("degraded", $"{Stats.Pct(sample.Loss)} packet loss")
            : (sample.Avg ?? 0) >= _settings.LatencyAlert ? ("degraded", $"{Stats.Ms(sample.Avg)} average latency")
            : ("ok", string.Empty);
        state.Status = status;
        if (status != previous.Status)
        {
            if (status is "down" or "degraded")
            {
                Event(target, status, reason);
            }
            else if (previous.Status is "down" or "degraded")
            {
                Event(target, "recovered", $"{Stats.Pct(sample.Loss)} loss, {Stats.Ms(sample.Avg)} avg");
            }
        }

        // Route changes need to show up twice in a row, so load balancing doesn't cause alerts
        if (sample.Reached)
        {
            if (previous.Route is null)
            {
                state.Route = sample.Route;
            }
            else if (Stats.RoutesDiffer(previous.Route, sample.Route))
            {
                if (previous.PendingRoute is not null && !Stats.RoutesDiffer(previous.PendingRoute, sample.Route))
                {
                    Event(target, "route", DescribeRouteChange(previous.Route, sample.Route), previous.Route, sample.Route);
                    state.Route = sample.Route;
                    state.PendingRoute = null;
                }
                else
                {
                    state.PendingRoute = sample.Route;
                }
            }
            else
            {
                state.PendingRoute = null;
            }
        }

        return state;
    }
}

/// <summary>Small dependency-free SVG charts (details pane and HTML report) and the static report.</summary>
public static class MtrReport
{
    public static string DataUri(string svg) => $"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}";

    public static string LatencyChart(IReadOnlyList<double?> latency, IReadOnlyList<double?>? low = null, IReadOnlyList<double?>? high = null, IReadOnlyList<double>? loss = null, int width = 640, int height = 180, string leftLabel = "", string rightLabel = "", string text = "#8a8f98")
    {
        const string Line = "#3b82f6";
        const string Band = "rgba(59,130,246,0.18)";
        const string Loss = "rgba(239,68,68,0.75)";
        const string Grid = "rgba(136,136,136,0.35)";
        var (padL, padR, padT, padB) = (44, 8, 10, 20);
        var plotW = width - padL - padR;
        var plotH = height - padT - padB;
        var n = Math.Max(Math.Max(latency.Count, loss?.Count ?? 0), 2);
        var all = latency.Concat(high ?? []).Where(v => v is not null).Select(v => v!.Value).ToList();
        var maxY = all.Count > 0 ? NiceMax(all.Max() * 1.1) : 10;
        double X(int i) => padL + (i / (double)(n - 1) * plotW);
        double Y(double v) => padT + plotH - (Math.Min(v, maxY) / maxY * plotH);
        string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        for (var i = 0; i <= 4; i++)
        {
            var value = maxY / 4 * i;
            var yy = Y(value);
            sb.Append($"<line x1=\"{padL}\" x2=\"{width - padR}\" y1=\"{F(yy)}\" y2=\"{F(yy)}\" stroke=\"{Grid}\" stroke-width=\"1\"/>");
            sb.Append($"<text x=\"{padL - 6}\" y=\"{F(yy + 4)}\" font-size=\"11\" text-anchor=\"end\" fill=\"{text}\">{(maxY < 10 && value > 0 ? value.ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(value).ToString(CultureInfo.InvariantCulture))} ms</text>");
        }

        if (all.Count == 0)
        {
            sb.Append($"<text x=\"{padL + (plotW / 2)}\" y=\"{padT + (plotH / 2)}\" font-size=\"13\" text-anchor=\"middle\" fill=\"{text}\">No latency data</text>");
        }

        if (loss is not null)
        {
            var barW = Math.Max(1, plotW / (double)n);
            for (var i = 0; i < loss.Count; i++)
            {
                if (loss[i] > 0)
                {
                    var h = Math.Min(loss[i], 100) / 100 * plotH;
                    sb.Append($"<rect x=\"{F(X(i) - (barW / 2))}\" y=\"{F(padT + plotH - h)}\" width=\"{F(barW)}\" height=\"{F(h)}\" fill=\"{Loss}\"/>");
                }
            }
        }

        if (low is not null && high is not null)
        {
            foreach (var seg in Segments(low.Select((v, i) => v is not null && high[i] is not null ? i : -1)))
            {
                var top = seg.Select(i => $"{F(X(i))},{F(Y(high[i]!.Value))}");
                var bottom = Enumerable.Reverse(seg).Select(i => $"{F(X(i))},{F(Y(low[i]!.Value))}");
                sb.Append($"<polygon points=\"{string.Join(" ", top.Concat(bottom))}\" fill=\"{Band}\"/>");
            }
        }

        foreach (var seg in Segments(latency.Select((v, i) => v is not null ? i : -1)))
        {
            sb.Append(seg.Count == 1
                ? $"<circle cx=\"{F(X(seg[0]))}\" cy=\"{F(Y(latency[seg[0]]!.Value))}\" r=\"2\" fill=\"{Line}\"/>"
                : $"<polyline points=\"{string.Join(" ", seg.Select(i => $"{F(X(i))},{F(Y(latency[i]!.Value))}"))}\" fill=\"none\" stroke=\"{Line}\" stroke-width=\"1.6\" stroke-linejoin=\"round\"/>");
        }

        if (leftLabel.Length > 0)
        {
            sb.Append($"<text x=\"{padL}\" y=\"{height - 4}\" font-size=\"11\" fill=\"{text}\">{WebUtility.HtmlEncode(leftLabel)}</text>");
        }

        if (rightLabel.Length > 0)
        {
            sb.Append($"<text x=\"{width - padR}\" y=\"{height - 4}\" font-size=\"11\" text-anchor=\"end\" fill=\"{text}\">{WebUtility.HtmlEncode(rightLabel)}</text>");
        }

        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" font-family=\"Segoe UI, sans-serif\">{sb}</svg>";
    }

    /// <summary>Puts samples into fixed time slots so gaps (PC asleep, offline) stay visible.</summary>
    public static string WindowChart(IReadOnlyList<Sample> samples, double hours, int width = 640)
    {
        var to = DateTimeOffset.Now;
        var from = to.AddHours(-hours);
        var slots = hours <= 1 ? 60 : hours <= 24 ? 288 : 336;
        var size = (to - from).TotalMilliseconds / slots;
        var groups = Enumerable.Range(0, slots).Select(_ => new List<Sample>()).ToArray();
        foreach (var s in samples)
        {
            var index = (int)Math.Floor((s.T - from).TotalMilliseconds / size);
            if (index >= 0 && index < slots)
            {
                groups[index].Add(s);
            }
        }

        double? Mean(IEnumerable<double> values)
        {
            var list = values.ToList();
            return list.Count > 0 ? list.Average() : null;
        }

        return LatencyChart(
            [.. groups.Select(g => Mean(g.Where(s => s.Avg is not null).Select(s => s.Avg!.Value)))],
            [.. groups.Select(g => g.Where(s => s.Best is not null).Select(s => (double?)s.Best!.Value).DefaultIfEmpty(null).Min())],
            [.. groups.Select(g => g.Where(s => s.Worst is not null).Select(s => (double?)s.Worst!.Value).DefaultIfEmpty(null).Max())],
            [.. groups.Select(g => Mean(g.Select(s => s.Loss)) ?? 0)],
            width,
            leftLabel: hours > 24 ? from.ToLocalTime().ToString("dd.MM HH:mm") : from.ToLocalTime().ToString("HH:mm"),
            rightLabel: hours > 24 ? to.ToLocalTime().ToString("dd.MM HH:mm") : "now");
    }

    public static string Build(MtrStore store)
    {
        string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        var events = store.Events(100);
        var cards = new StringBuilder();
        foreach (var t in store.Targets())
        {
            var state = store.State(t.Id);
            var samples = store.Samples(t.Id, DateTimeOffset.Now.AddDays(-7));
            var last = state.LastSample;
            var day = samples.Where(s => s.T >= DateTimeOffset.Now.AddDays(-1)).ToList();
            var dayLoss = day.Count > 0 ? day.Average(s => s.Loss) : 0;
            var dayAvg = day.Where(s => s.Avg is not null).Select(s => s.Avg!.Value).DefaultIfEmpty().Average();
            var hops = string.Concat((last?.Hops ?? []).Select((h, i) =>
            {
                var name = h.Addr is not null && state.Hosts.TryGetValue(h.Addr, out var n) ? n : null;
                return $"<tr><td>{i + 1}</td><td>{(h.Addr is null ? "???" : E(name ?? h.Addr))}{(name is null ? string.Empty : $"<small>{E(h.Addr)}</small>")}</td><td class=\"{(h.Loss >= 20 ? "bad" : h.Loss > 0 ? "warn" : string.Empty)}\">{Stats.Pct(h.Loss)}</td><td>{Stats.Ms(h.Avg)}</td></tr>";
            }));
            var targetEvents = string.Concat(events.Where(e => e.Target == t.Id).Take(8).Select(e => $"<li>{Icon(e.Kind)} <time>{e.T.ToLocalTime():dd.MM HH:mm}</time> {E(e.Message)}</li>"));
            cards.Append($"""
<section class="card">
  <header><div><h2>{E(t.Name)}</h2><span class="sub">{E(t.Host)}{(last is not null && last.Ip != t.Host ? $" · {E(last.Ip)}" : string.Empty)}</span></div><span class="badge {state.Status}">{StatusLabel(state.Status)}</span></header>
  <div class="stats">
    <div><b>{(last is null ? "–" : Stats.Pct(last.Loss))}</b><span>loss now</span></div><div><b>{Stats.Ms(last?.Avg)}</b><span>avg now</span></div>
    <div><b>{Stats.Ms(last?.Jitter)}</b><span>jitter</span></div><div><b>{Stats.Pct(dayLoss)}</b><span>loss 24 h</span></div>
    <div><b>{Stats.Ms(dayAvg > 0 ? dayAvg : null)}</b><span>avg 24 h</span></div><div><b>{last?.Hops.Length.ToString() ?? "–"}</b><span>hops</span></div>
  </div>
  {(state.Error is null ? string.Empty : $"<p class=\"error\">{E(state.Error)}</p>")}
  <h3>Last 24 hours</h3><div class="chart">{WindowChart(samples, 24, 900)}</div>
  <details><summary>Last 7 days</summary><div class="chart">{WindowChart(samples, 168, 900)}</div></details>
  <div class="cols">
    <div><h3>Route</h3><table><thead><tr><th>#</th><th>Host</th><th>Loss</th><th>Avg</th></tr></thead><tbody>{(hops.Length > 0 ? hops : "<tr><td colspan=\"4\">No data yet</td></tr>")}</tbody></table></div>
    <div><h3>Events</h3><ul class="events">{(targetEvents.Length > 0 ? targetEvents : "<li>No events</li>")}</ul></div>
  </div>
</section>
""");
        }

        return $$"""
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta http-equiv="refresh" content="60"><title>MTR Monitor</title>
<style>
:root { --bg:#f6f7f9; --card:#fff; --text:#1d1f24; --muted:#6b7280; --line:#e5e7eb; }
@media (prefers-color-scheme: dark) { :root { --bg:#14161a; --card:#1d2026; --text:#e8eaee; --muted:#9aa1ad; --line:#2c3038; } }
* { box-sizing:border-box; } body { margin:0; padding:24px 16px; background:var(--bg); color:var(--text); font:14px/1.45 "Segoe UI", system-ui, sans-serif; }
main { max-width:960px; margin:0 auto; } h1 { font-size:22px; margin:0 0 4px; } h2 { font-size:18px; margin:0; } h3 { font-size:13px; text-transform:uppercase; letter-spacing:.04em; color:var(--muted); margin:18px 0 8px; }
.top { display:flex; justify-content:space-between; align-items:baseline; margin-bottom:16px; color:var(--muted); }
.card { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:16px 18px; margin-bottom:18px; } .card header { display:flex; justify-content:space-between; align-items:center; gap:12px; }
.sub, small { color:var(--muted); font-size:12px; } td small { display:block; }
.badge { padding:3px 10px; border-radius:99px; font-weight:600; font-size:12px; white-space:nowrap; } .badge.ok { background:#16a34a22; color:#16a34a; } .badge.degraded { background:#f59e0b22; color:#d97706; } .badge.down { background:#ef444422; color:#ef4444; } .badge.unknown { background:#8882; color:var(--muted); }
.stats { display:grid; grid-template-columns:repeat(auto-fit, minmax(110px, 1fr)); gap:10px; margin-top:14px; } .stats div { border:1px solid var(--line); border-radius:8px; padding:8px 10px; } .stats b { display:block; font-size:18px; } .stats span { color:var(--muted); font-size:12px; }
.chart svg { width:100%; height:auto; display:block; } details summary { cursor:pointer; color:var(--muted); margin-top:10px; }
.cols { display:grid; grid-template-columns:1fr 1fr; gap:18px; } @media (max-width:700px) { .cols { grid-template-columns:1fr; } }
table { width:100%; border-collapse:collapse; } th, td { text-align:left; padding:4px 6px; border-bottom:1px solid var(--line); vertical-align:top; } th { color:var(--muted); font-weight:600; font-size:12px; } td.bad { color:#ef4444; font-weight:600; } td.warn { color:#d97706; }
.events { list-style:none; padding:0; margin:0; } .events li { padding:4px 0; border-bottom:1px solid var(--line); } time { color:var(--muted); font-size:12px; margin-right:4px; } .error { color:#ef4444; }
</style></head>
<body><main>
<div class="top"><h1>MTR Monitor</h1><span>Updated {{DateTime.Now:g}} · refreshes every minute</span></div>
{{(cards.Length > 0 ? cards.ToString() : "<p>No targets yet. Add some with the Command Palette command “Monitored Targets”.</p>")}}
</main></body></html>
""";
    }

    public static string StatusLabel(string status) => status switch { "ok" => "OK", "degraded" => "Degraded", "down" => "Down", _ => "Not checked" };

    public static string Icon(string kind) => kind switch { "down" => "🔴", "degraded" => "🟠", "recovered" => "🟢", "route" => "🔀", _ => "⚠️" };

    private static double NiceMax(double value)
    {
        if (value <= 0)
        {
            return 1;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in new[] { 1, 2, 2.5, 5, 10 })
        {
            if (step * magnitude >= value)
            {
                return step * magnitude;
            }
        }

        return 10 * magnitude;
    }

    private static List<List<int>> Segments(IEnumerable<int> indices)
    {
        var segments = new List<List<int>>();
        var current = new List<int>();
        foreach (var i in indices)
        {
            if (i < 0)
            {
                if (current.Count > 0)
                {
                    segments.Add(current);
                }

                current = [];
            }
            else
            {
                current.Add(i);
            }
        }

        if (current.Count > 0)
        {
            segments.Add(current);
        }

        return segments;
    }
}
