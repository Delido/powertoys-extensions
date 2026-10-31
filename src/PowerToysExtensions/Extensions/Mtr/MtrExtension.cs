using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.Mtr;

[Guid("B03F6BE3-D6A0-4CDB-960C-61F656A4AC25")]
public sealed partial class MtrExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new MtrProvider();
}

public sealed partial class MtrProvider : CommandProvider
{
    private readonly MtrMonitor _monitor;
    private readonly CommandItem _monitorItem;
    private readonly ICommandItem[] _commands;

    public MtrProvider()
    {
        var settings = new MtrSettings();
        var store = new MtrStore();
        _monitor = new MtrMonitor(settings, store);
        Id = "delido.mtr";
        DisplayName = "MTR";
        Icon = Ui.Glyph("");
        Settings = settings.Settings;

        _monitorItem = new CommandItem(new AnonymousCommand(() => Status.Start("Checking targets …", "Check finished", async () => Status.Info($"MTR: {await _monitor.Check(true)}")))
        {
            Id = "monitor",
            Name = "Check Now",
            Result = CommandResult.KeepOpen(),
        })
        {
            Title = "MTR Background Monitor",
            Icon = Ui.Glyph(""),
            Subtitle = _monitor.Status,
            MoreCommands = [new CommandContextItem(Ui.OpenSettings("delido.mtr")) { Title = "Monitor Settings" }],
        };
        _monitor.StatusChanged += s => _monitorItem.Subtitle = s;
        _commands =
        [
            new CommandItem(new TracePickerPage(settings, store)) { Title = "Trace Route", Subtitle = "MTR · live traceroute with statistics" },
            new CommandItem(new TargetsPage(settings, store, _monitor)) { Title = "Monitored Targets", Subtitle = "MTR" },
            _monitorItem,
            new CommandItem(new AnonymousCommand(() => OpenReport(store)) { Id = "open-report", Name = "Open", Result = CommandResult.Dismiss() }) { Title = "Open MTR Report", Icon = Ui.Glyph("") },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem? GetCommandItem(string id) => _commands.FirstOrDefault(c => c.Command?.Id == id);

    public override void Dispose()
    {
        _monitor.Dispose();
        base.Dispose();
    }

    internal static void OpenReport(MtrStore store)
    {
        File.WriteAllText(store.ReportPath, MtrReport.Build(store));
        Proc.Open(store.ReportPath);
    }
}

internal sealed partial class TracePickerPage : DynamicListPage
{
    private static readonly string RecentFile = Path.Combine(Paths.For("mtr"), "recent.json");
    private readonly MtrSettings _settings;
    private readonly MtrStore _store;

    public TracePickerPage(MtrSettings settings, MtrStore store)
    {
        _settings = settings;
        _store = store;
        Id = "trace";
        Name = "Open";
        Title = "Trace Route";
        Icon = Ui.Glyph("");
        PlaceholderText = "Host name or IP address, e.g. 1.1.1.1";
    }

    public static void Remember(string target)
    {
        try
        {
            var recent = Recent().Where(t => !t.Equals(target, StringComparison.OrdinalIgnoreCase)).Prepend(target).Take(10);
            File.WriteAllText(RecentFile, JsonSerializer.Serialize(recent));
        }
        catch (IOException)
        {
            // not important
        }
    }

    public override IListItem[] GetItems()
    {
        var items = new List<IListItem>();
        var text = SearchText.Trim();
        if (text.Length > 0 && !text.Contains(' ', StringComparison.Ordinal))
        {
            items.Add(Item(text, $"Trace {text}", string.Empty, "", "Trace"));
        }

        items.AddRange(_store.Targets().Where(t => Matches(t.Host) || Matches(t.Label)).Select(t => Item(t.Host, t.Name, t.Label is null ? string.Empty : t.Host, "", "Monitored")));
        items.AddRange(Recent().Where(h => Matches(h)).Select(h => Item(h, h, string.Empty, "", "Recent")));
        return [.. items];
    }

    public override void UpdateSearchText(string oldSearch, string newSearch) => RaiseItemsChanged();

    private static string[] Recent()
    {
        try
        {
            return File.Exists(RecentFile) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(RecentFile)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    private bool Matches(string? value) => SearchText.Trim().Length == 0 || (value?.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);

    private ListItem Item(string host, string title, string subtitle, string glyph, string section) =>
        new(new TracePage(host, _settings, _store)) { Title = title, Subtitle = subtitle, Icon = Ui.Glyph(glyph), Section = section };
}

/// <summary>
/// Live trace: one probe round per interval while the page is shown. Command Palette stops asking for items
/// when the page is closed, so the probe pauses when GetItems has not been called for a while.
/// </summary>
internal sealed partial class TracePage : ListPage
{
    private readonly string _target;
    private readonly MtrSettings _settings;
    private readonly MtrStore _store;
    private readonly Dictionary<string, string> _hosts = [];
    private Aggregator _agg = new();
    private HopRow[] _rows = [];
    private CancellationTokenSource? _cts;
    private DateTime _lastSeen;
    private bool _paused;
    private string? _ip;

    public TracePage(string target, MtrSettings settings, MtrStore store)
    {
        _target = target;
        _settings = settings;
        _store = store;
        Name = "Start Trace";
        Title = target;
        Icon = Ui.Glyph("");
        PlaceholderText = "Filter hops …";
        ShowDetails = true;
        EmptyContent = Ui.EmptyItem("Tracing …", target, "");
    }

    public override IListItem[] GetItems()
    {
        _lastSeen = DateTime.UtcNow;
        if (_cts is null && !_paused)
        {
            TracePickerPage.Remember(_target);
            _cts = new CancellationTokenSource();
            _ = Loop(_cts.Token);
        }

        HopRow[] rows;
        lock (_hosts)
        {
            rows = _rows;
        }

        var reached = rows.LastOrDefault()?.Addr is { } a && a == _ip;
        Title = $"{_target}{(_ip is not null && _ip != _target ? $" ({_ip})" : string.Empty)} · {_agg.Rounds} rounds{(_paused ? " · paused" : string.Empty)}";
        return [.. rows.Select(r => Item(r, reached || rows.Length == 0 ? "Hops" : "Hops (target not reached)", rows))];
    }

    private async Task Loop(CancellationToken token)
    {
        try
        {
            var probe = _settings.Probe(_target);
            probe.HostResolved += (addr, name) =>
            {
                lock (_hosts)
                {
                    _hosts[addr] = name;
                }
            };
            _ip = (await probe.Resolve(token)).ToString();
            for (var round = 1; !token.IsCancellationRequested; round++)
            {
                // the page was closed: stop probing until it is shown again
                if (DateTime.UtcNow - _lastSeen > TimeSpan.FromSeconds(15))
                {
                    break;
                }

                var start = DateTime.UtcNow;
                var result = await probe.Run(round, token);
                lock (_hosts)
                {
                    _agg.Add(result);
                    _rows = _agg.Rows();
                }

                RaiseItemsChanged();
                var wait = TimeSpan.FromMilliseconds(_settings.TraceInterval) - (DateTime.UtcNow - start);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // paused
        }
        catch (Exception ex)
        {
            EmptyContent = Ui.EmptyItem("Trace failed", ex.Message, "");
            RaiseItemsChanged();
        }
        finally
        {
            _cts = null;
        }
    }

    private void TogglePause()
    {
        _paused = !_paused;
        if (_paused)
        {
            _cts?.Cancel();
        }

        RaiseItemsChanged();
    }

    private void Reset()
    {
        lock (_hosts)
        {
            _agg = new Aggregator();
            _rows = [];
        }

        RaiseItemsChanged();
    }

    private ListItem Item(HopRow row, string section, HopRow[] rows)
    {
        Dictionary<string, string> hosts;
        lock (_hosts)
        {
            hosts = new Dictionary<string, string>(_hosts);
        }

        var name = row.Addr is null ? "???" : hosts.GetValueOrDefault(row.Addr) ?? row.Addr;
        var isTarget = row.Addr is not null && row.Addr == _ip;
        var seconds = Math.Round(row.Rtts.Length * _settings.TraceInterval / 1000.0);
        var chart = MtrReport.LatencyChart(row.Rtts, loss: [.. row.Rtts.Select(v => v is null ? 100.0 : 0)], width: 520, height: 170, leftLabel: $"{seconds}s ago", rightLabel: "now");
        var addrs = row.Addrs.OrderByDescending(p => p.Value).ToList();
        var meta = new List<IDetailsElement>
        {
            Ui.Label("Hop", $"{row.Ttl}. {name}"),
            Ui.Tags("Loss", Stats.LossTag(row.Loss)),
            Ui.Label("Sent / lost", $"{row.Sent} / {row.Sent - row.Recv}"),
            Ui.Label("Last", Stats.Ms(row.Last)),
            Ui.Label("Best / Avg / Worst", $"{Stats.Ms(row.Best)} / {Stats.Ms(row.Avg)} / {Stats.Ms(row.Worst)}"),
            Ui.Label("StDev", Stats.Ms(row.Stdev)),
            Ui.Label("Jitter", Stats.Ms(row.Jitter)),
            Ui.Separator(),
        };
        meta.AddRange(addrs.Count == 0
            ? [Ui.Label("Address", "No reply")]
            : addrs.Select(p => Ui.Label(addrs.Count > 1 ? $"Address ({Math.Round(p.Value * 100.0 / Math.Max(1, row.Recv))}%)" : "Address", hosts.TryGetValue(p.Key, out var n) ? $"{p.Key} · {n}" : p.Key)));

        var commands = new List<IContextItem>
        {
            Ui.Action(_paused ? "Resume" : "Pause", _paused ? "" : "", TogglePause, Ui.Ctrl(VirtualKey.P)),
            Ui.Action("Reset Statistics", "", Reset, Ui.CtrlShift(VirtualKey.R)),
            Ui.Copy(Stats.AsTable(_target, rows, hosts), "Copy as Text Table", Ui.CtrlShift(VirtualKey.C)),
            Ui.Action("Monitor This Target", "", () =>
            {
                _store.AddTarget(_target, null);
                Status.Info($"Monitoring {_target} – switch the background monitor on in the settings");
            }, Ui.Ctrl(VirtualKey.S)),
        };
        if (row.Addr is not null)
        {
            commands.Add(Ui.Copy(row.Addr, "Copy Address", Ui.CtrlShift((VirtualKey)0xBE)));
            if (!isTarget)
            {
                commands.Add(new CommandContextItem(new TracePage(row.Addr, _settings, _store)) { Title = "Trace to This Hop" });
            }
        }

        return new ListItem(new NoOpCommand())
        {
            Title = $"{row.Ttl}. {name}",
            Subtitle = row.Addr is not null && hosts.ContainsKey(row.Addr) ? row.Addr : string.Empty,
            Icon = Ui.Glyph(isTarget ? "" : row.Addr is null ? "" : ""),
            Section = section,
            Tags = [Ui.Tag(Stats.Sparkline(row.Rtts, 15)), Stats.LossTag(row.Loss), Ui.Tag(Stats.Ms(row.Avg))],
            Details = new Details { Title = $"{row.Ttl}. {name}", Body = $"![Latency]({MtrReport.DataUri(chart)})\n\n```\n{Stats.Sparkline(row.Rtts, 60)}\n```", Metadata = [.. meta], Size = ContentSize.Large },
            MoreCommands = [.. commands],
        };
    }
}

internal sealed partial class TargetsPage : AsyncListPage
{
    private readonly MtrSettings _settings;
    private readonly MtrStore _store;
    private readonly MtrMonitor _monitor;
    private readonly ChoiceFilters _range = new(("24", "Last 24 hours"), ("1", "Last hour"), ("168", "Last 7 days"));

    public TargetsPage(MtrSettings settings, MtrStore store, MtrMonitor monitor)
    {
        _settings = settings;
        _store = store;
        _monitor = monitor;
        Id = "targets";
        Name = "Open";
        Title = "Monitored Targets";
        Icon = Ui.Glyph("");
        PlaceholderText = "Filter targets …";
        ShowDetails = true;
        Filters = _range;
        _range.Changed += Reload;
        MaxAge = TimeSpan.FromSeconds(30);
    }

    protected override Task<Batch> Load(int page, CancellationToken token) => Task.Run(() =>
    {
        var hours = double.Parse(_range.CurrentFilterId, System.Globalization.CultureInfo.InvariantCulture);
        return new Batch([.. _store.Targets().Select(t => Item(t, hours))]);
    });

    protected override ICommandItem? Empty() => new CommandItem(TargetForm(null).Command!)
    {
        Title = "No monitored targets",
        Subtitle = "Enter adds a target; then switch the background monitor on in the settings",
        Icon = Ui.Glyph(""),
    };

    private CommandContextItem TargetForm(Target? target)
    {
        var form = new FormBuilder()
            .Input("host", "Host", target?.Host ?? string.Empty, "1.1.1.1 or example.com", required: true)
            .Input("label", "Name", target?.Label ?? string.Empty, "optional, e.g. Cloudflare DNS")
            .Text("The background monitor checks all targets. Switch it on in the settings of this extension.", subtle: true)
            .Build(target is null ? "Add Target" : "Save");
        return new CommandContextItem(new FormPage(target is null ? "Add Target" : "Edit Target", form, inputs =>
        {
            var host = inputs.Str("host");
            if (host.Length == 0 || host.Contains(' ', StringComparison.Ordinal))
            {
                return CommandResult.ShowToast(new ToastArgs { Message = "Enter a host name or IPv4 address", Result = CommandResult.KeepOpen() });
            }

            if (target is null)
            {
                _store.AddTarget(host, inputs.Str("label"));
            }
            else
            {
                _store.UpdateTarget(target.Id, host, inputs.Str("label"));
            }

            Reload();
            return CommandResult.GoBack();
        }, Ui.Glyph("")))
        {
            Title = target is null ? "Add Target" : "Edit Target",
            RequestedShortcut = target is null ? Ui.Ctrl(VirtualKey.N) : Ui.Ctrl(VirtualKey.E),
        };
    }

    private ListItem Item(Target target, double hours)
    {
        var state = _store.State(target.Id);
        var samples = _store.Samples(target.Id, DateTimeOffset.Now.AddDays(-7));
        var inRange = samples.Where(s => s.T >= DateTimeOffset.Now.AddHours(-hours)).ToList();
        var reached = inRange.Where(s => s.Avg is not null).ToList();
        double? avg = reached.Count > 0 ? reached.Average(s => s.Avg!.Value) : null;
        var loss = inRange.Count > 0 ? inRange.Average(s => s.Loss) : 0;
        var last = state.LastSample;
        var hops = string.Join("\n", (last?.Hops ?? []).Select((h, i) => $"| {i + 1} | {(h.Addr is null ? "???" : state.Hosts.GetValueOrDefault(h.Addr) ?? h.Addr)} | {Stats.Pct(h.Loss)} | {Stats.Ms(h.Avg)} |"));
        var body = $"![Latency]({MtrReport.DataUri(MtrReport.WindowChart(samples, hours, 520))})\n\n```\n{Stats.Sparkline(inRange.TakeLast(60).Select(s => s.Avg), 60)}\n```\n\n"
            + (last is null ? "_Not checked yet_" : $"**Route** · {last.T.ToLocalTime():HH:mm}\n\n| # | Host | Loss | Avg |\n|---|---|---|---|\n{hops}");
        var statusTag = state.Status switch { "ok" => Ui.Green("OK"), "degraded" => Ui.Orange("Degraded"), "down" => Ui.Red("Down"), _ => Ui.Gray("Not checked yet") };

        var commands = new List<IContextItem>
        {
            new CommandContextItem(new EventsPage(_store, target)) { Title = "Show Events", RequestedShortcut = Ui.Ctrl(VirtualKey.L) },
            TargetForm(null),
            Ui.Action("Check All Now", "", () => Status.Start("Checking targets …", "Check finished", async () => Status.Info($"MTR: {await _monitor.Check(true)}"), Reload), Ui.Ctrl(VirtualKey.R)),
            Ui.Action("Open Report in Browser", "", () => MtrProvider.OpenReport(_store), Ui.Ctrl(VirtualKey.O)),
            new CommandContextItem(new EventsPage(_store, null)) { Title = "Show All Events", RequestedShortcut = Ui.CtrlShift(VirtualKey.L) },
            TargetForm(target),
        };
        if (state.Route is not null)
        {
            commands.Add(Ui.Action("Accept Current Route", "", () =>
            {
                _store.ResetRoute(target.Id);
                Status.Info("The next check stores the route as the new normal");
            }));
        }

        commands.Add(Ui.Confirm("Clear History", "", $"Clear the history of {target.Name}?", "All stored measurements of this target are deleted.", () =>
        {
            _store.ClearSamples(target.Id);
            Reload();
        }, critical: true));
        commands.Add(Ui.Confirm("Remove Target", "", $"Remove {target.Name}?", "Its history is deleted too.", () =>
        {
            _store.RemoveTarget(target.Id);
            Reload();
        }, critical: true, shortcut: Ui.Ctrl(VirtualKey.Delete)));

        return new ListItem(new TracePage(target.Host, _settings, _store) { Name = "Live Trace" })
        {
            Title = target.Name,
            Subtitle = target.Label is null ? string.Empty : target.Host,
            Icon = Ui.Glyph(state.Status switch { "ok" => "", "degraded" => "", "down" => "", _ => "" }),
            Tags = [Ui.Tag(Stats.Ms(last?.Avg)), statusTag],
            Details = new Details
            {
                Title = target.Name,
                Body = body,
                Size = ContentSize.Large,
                Metadata =
                [
                    Ui.Tags("Status", statusTag),
                    Ui.Label("Host", last?.Ip is { } ip && ip != target.Host ? $"{target.Host} ({ip})" : target.Host),
                    Ui.Label("Error", state.Error),
                    Ui.Separator(),
                    Ui.Label("Now", last is null ? "–" : $"{Stats.Pct(last.Loss)} loss · {Stats.Ms(last.Avg)} · jitter {Stats.Ms(last.Jitter)}"),
                    Ui.Label(hours == 168 ? "Last 7 days" : $"Last {hours} h", $"{Stats.Pct(loss)} loss · {Stats.Ms(avg)} avg"),
                    Ui.Label("Checks", inRange.Count.ToString()),
                    Ui.Label("Last check", state.LastCheck?.ToLocalTime().ToString("g")),
                ],
            },
            MoreCommands = [.. commands],
        };
    }
}

internal sealed partial class EventsPage : AsyncListPage
{
    private readonly MtrStore _store;
    private readonly Target? _target;

    public EventsPage(MtrStore store, Target? target)
    {
        _store = store;
        _target = target;
        Name = target is null ? "Show All Events" : "Show Events";
        Title = target is null ? "All Events" : $"Events · {target.Name}";
        Icon = Ui.Glyph("");
        EmptyContent = Ui.EmptyItem("No events", "Outages, recoveries and route changes appear here", "");
    }

    protected override Task<Batch> Load(int page, CancellationToken token) => Task.FromResult(new Batch([.. _store.Events(500, _target?.Id).Select(e =>
        new ListItem(new CopyTextCommand($"{e.T.ToLocalTime():g} {e.Host}: {e.Message}{(e.From is null ? string.Empty : $"\nfrom {e.From}\nto   {e.To}")}") { Name = "Copy Event" })
        {
            Title = $"{MtrReport.Icon(e.Kind)} {e.Message}",
            Subtitle = _target is null ? e.Host : string.Empty,
            Tags = [Ui.Tag(Ui.Ago(e.T))],
        })]));
}
