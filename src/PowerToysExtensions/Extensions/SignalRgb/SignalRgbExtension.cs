using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.SignalRgb;

[Guid("8082F478-79FE-4756-8086-CBC290CF8F26")]
public sealed partial class SignalRgbExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new SignalRgbProvider();
}

public sealed partial class SignalRgbProvider : CommandProvider
{
    public const string ProviderId = "delido.signalrgb";
    private readonly Companion _companion;
    private readonly ICommandItem[] _commands;
    private readonly CommandItem _companionItem;
    private readonly CommandItem _band;
    private readonly Timer _watchdog;
    private int _alertCount = -1;
    private int _alertPid;

    public SignalRgbProvider()
    {
        var settings = new SignalSettings();
        var api = new SignalApi(settings);
        _companion = new Companion(settings);
        Id = ProviderId;
        DisplayName = "SignalRGB";
        Icon = Ui.Glyph("");
        Settings = settings.Settings;

        _companionItem = new CommandItem(new CompanionPage(_companion)) { Title = "Headset & Companion" };
        _band = new CommandItem(new AnonymousCommand(() => ToggleMic()) { Id = "mic-band", Name = "Toggle Microphone", Result = CommandResult.KeepOpen() }) { Title = "Mic" };
        _commands =
        [
            new CommandItem(new SensorsPage(api)) { Title = "PC Sensors", Subtitle = "SignalRGB · live temperatures, fans, load" },
            new CommandItem(new EffectsPage(api)) { Title = "Lighting Effects", Subtitle = "SignalRGB" },
            Quick("next-effect", "Next Effect", "", async () => $"🎨 {await api.Step("next")}"),
            Quick("previous-effect", "Previous Effect", "", async () => $"🎨 {await api.Step("previous")}"),
            Quick("random-effect", "Random Effect", "", async () => $"🎨 {await api.Step("shuffle")}"),
            Quick("toggle-lighting", "Toggle Lighting", "", async () =>
            {
                var enabled = (await api.GetLighting()).Enabled;
                await api.SetEnabled(!enabled);
                return enabled ? "💡 Lighting off" : "💡 Lighting on";
            }),
            new CommandItem(new BrightnessPage(api)) { Title = "Set Brightness", Subtitle = "SignalRGB" },
            new CommandItem(new AudioOutputPage(_companion)) { Title = "Switch Audio Output", Subtitle = "SignalRGB companion" },
            Quick("next-audio-output", "Next Audio Output", "", async () =>
            {
                await _companion.NextAudioDevice();
                return await _companion.CurrentAudioDevice() is { } name ? $"🔊 {Companion.SplitDeviceName(name).Title}" : "🔊 Switched audio output";
            }),
            _companionItem,
            new CommandItem(new AnonymousCommand(() => ToggleMic()) { Id = "toggle-mic", Name = "Toggle", Result = CommandResult.KeepOpen() }) { Title = "Toggle Microphone Mute", Icon = Ui.Glyph("") },
        ];

        // replaces Raycast's "Companion Service" background command: keep the helper alive and its config in sync
        _watchdog = new Timer(_ => _ = Watch(), null, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(1));
        settings.Changed += () => _ = Watch();
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem[]? GetDockBands() => [_band];

    public override ICommandItem? GetCommandItem(string id) => _commands.Concat([_band]).FirstOrDefault(c => c.Command?.Id == id);

    public override void Dispose()
    {
        _watchdog.Dispose();
        base.Dispose();
    }

    private static CommandItem Quick(string id, string title, string glyph, Func<Task<string>> action) =>
        new(new AnonymousCommand(() => _ = Task.Run(async () =>
        {
            try
            {
                Status.Info(await action());
            }
            catch (Exception ex)
            {
                Status.Error($"SignalRGB: {ex.Message}");
            }
        }))
        {
            Id = id,
            Name = title,
            Result = CommandResult.KeepOpen(),
        })
        {
            Title = title,
            Subtitle = "SignalRGB",
            Icon = Ui.Glyph(glyph),
        };

    private void ToggleMic() => _ = Task.Run(async () =>
    {
        try
        {
            var muted = await _companion.SetMic("toggle");
            Status.Info(muted ? "🔇 Microphone muted" : "🎤 Microphone live");
            await Watch();
        }
        catch (Exception ex)
        {
            Status.Error($"Companion service not running: {ex.Message}");
        }
    });

    private async Task Watch()
    {
        try
        {
            if (!_companion.Settings.ServiceEnabled)
            {
                _companionItem.Subtitle = "Service off (settings)";
                return;
            }

            _companion.WriteConfig();
            var status = await _companion.Status();
            if (status is null)
            {
                try
                {
                    await _companion.Start();
                    status = await _companion.Status();
                }
                catch (Exception ex)
                {
                    _companionItem.Subtitle = $"Not running: {ex.Message}";
                    return;
                }
            }

            if (status is null)
            {
                return;
            }

            var headset = status["headset"];
            var parts = new List<string>();
            if (headset?["battery"] is JsonValue battery)
            {
                parts.Add($"🎧 {battery}%");
            }

            if (headset?["windowsMuted"] is JsonValue muted)
            {
                parts.Add(muted.GetValue<bool>() ? "🔇 muted" : "🎤 live");
            }

            if (status["cs2"]?["gameRunning"]?.GetValue<bool>() == true)
            {
                parts.Add("CS2");
            }

            _companionItem.Subtitle = parts.Count > 0 ? string.Join(" · ", parts) : "Running";
            _band.Title = headset?["windowsMuted"]?.GetValue<bool>() == true ? "🔇" : "🎤";
            _band.Subtitle = headset?["battery"] is JsonValue b ? $"{b}%" : string.Empty;

            // the helper plays the alert sound itself; optionally add a Windows notification
            var pid = status["pid"]?.GetValue<int>() ?? 0;
            var count = headset?["alertCount"]?.GetValue<int>() ?? 0;
            if (_companion.Settings.BatteryNotify && _alertCount >= 0 && pid == _alertPid && count > _alertCount && headset?["battery"] is JsonValue level)
            {
                Notify.Show("🪫 Headset battery low", $"{headset["name"]}: {level}%", group: "signalrgb");
            }

            _alertCount = count;
            _alertPid = pid;
        }
        catch (Exception ex)
        {
            Log.Error("signalrgb watchdog", ex);
        }
    }
}

internal sealed partial class SensorsPage : ListPage
{
    private static readonly string FavoritesFile = Path.Combine(Paths.For("signalrgb"), "favorite-sensors.json");
    private static readonly (string Id, string Name)[] Categories =
        [("all", "All Sensors"), ("favorites", "Favorites"), ("temperature", "Temperatures"), ("cooling", "Fans & Pumps"), ("load", "Load & Memory"), ("power", "Power, Clocks & Voltage"), ("io", "Network & Disk")];

    private readonly SignalApi _api;
    private readonly ChoiceFilters _filter = new(Categories);
    private readonly Timer _idle;
    private HashSet<string> _favorites;
    private readonly Dictionary<string, SensorItem> _items = [];
    private Sensor[] _sensors = [];
    private string[] _layout = [];
    private WebPipe? _pipe;
    private DateTime _lastSeen;

    public SensorsPage(SignalApi api)
    {
        _api = api;
        Id = "pc-sensors";
        Name = "Open";
        Title = "PC Sensors";
        Icon = Ui.Glyph("");
        PlaceholderText = "Filter sensors …";
        ShowDetails = true;
        Filters = _filter;
        _filter.Changed += () => RaiseItemsChanged();
        EmptyContent = Ui.EmptyItem("Connecting to SignalRGB …", string.Empty, "");
        _favorites = LoadFavorites();

        // disconnect when the page is not shown anymore: a connected reader keeps SignalRGB busy
        _idle = new Timer(_ => _ = DisconnectIfIdle(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public override IListItem[] GetItems()
    {
        _lastSeen = DateTime.UtcNow;
        if (_pipe is null)
        {
            _ = Connect();
        }

        var (rows, visible) = Rows();
        if (_sensors.Length > 0 && rows.Count == 0)
        {
            EmptyContent = Ui.EmptyItem(_filter.CurrentFilterId == "favorites" ? "No favorites yet" : "No sensors", _filter.CurrentFilterId == "favorites" ? "Pin sensors with Ctrl+Shift+P" : string.Empty, "");
        }

        _layout = [.. rows.Select(r => r.Key)];
        return [.. rows.Select(r => Fill(r.Key, r.Sensor, r.Section, visible))];
    }

    /// <summary>The shown rows (favorites first in "All Sensors"), each keyed by section and sensor.</summary>
    private (List<(string Key, Sensor Sensor, string Section)> Rows, Sensor[] Visible) Rows()
    {
        var visible = _filter.CurrentFilterId switch
        {
            "all" => _sensors,
            "favorites" => _sensors.Where(s => _favorites.Contains(s.Uid)).ToArray(),
            var c => _sensors.Where(s => s.Category == c).ToArray(),
        };
        var rows = new List<(string Key, Sensor Sensor, string Section)>();
        if (_filter.CurrentFilterId == "all")
        {
            rows.AddRange(visible.Where(s => _favorites.Contains(s.Uid)).Select(s => ($"fav|{s.Uid}", s, "Favorites")));
        }

        rows.AddRange(visible.Select(s => ($"{s.Group}|{s.Uid}", s, s.Group)));
        return (rows, visible);
    }

    /// <summary>
    /// New values update the existing items in place; rebuilding the list every second would make
    /// Command Palette jump back to the top. Only a changed set of sensors rebuilds it.
    /// </summary>
    private void OnUpdate(Sensor[] sensors)
    {
        _sensors = sensors;
        var (rows, visible) = Rows();
        if (!rows.Select(r => r.Key).SequenceEqual(_layout))
        {
            RaiseItemsChanged();
            return;
        }

        foreach (var (key, sensor, section) in rows)
        {
            Fill(key, sensor, section, visible);
        }
    }

    private static HashSet<string> LoadFavorites()
    {
        try
        {
            return File.Exists(FavoritesFile) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(FavoritesFile)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    private static string Sparkline(IReadOnlyList<double> values, int width)
    {
        const string Bars = "▁▂▃▄▅▆▇█";
        var points = values.TakeLast(width).ToList();
        if (points.Count == 0)
        {
            return string.Empty;
        }

        var (low, high) = (points.Min(), points.Max());
        return high - low < 1e-9 ? new string(Bars[3], points.Count) : string.Concat(points.Select(v => Bars[(int)Math.Round((v - low) / (high - low) * (Bars.Length - 1))]));
    }

    private static string Round(double v) => Math.Abs(v) >= 100 ? v.ToString("0") : Math.Abs(v) >= 10 ? v.ToString("0.0") : v.ToString("0.00");

    private static string WithUnit(double value, string unit)
    {
        var label = unit switch { "C" => "°C", "Mhz" => "MHz", "mb" => "MB", _ => unit };
        return label.Length > 0 ? $"{Round(value)} {label}" : Round(value);
    }

    private static Tag ValueTag(Sensor s)
    {
        if (s.Type == "TemperatureProvider")
        {
            return s.Value <= 0 ? Ui.Gray(s.Display) : s.Value >= 85 ? Ui.Red(s.Display) : s.Value >= 70 ? Ui.Orange(s.Display) : Ui.Green(s.Display);
        }

        if (s.Unit == "%" && s.Category == "load")
        {
            return s.Value >= 90 ? Ui.Red(s.Display) : s.Value >= 70 ? Ui.Orange(s.Display) : Ui.Tag(s.Display);
        }

        return Ui.Tag(s.Display);
    }

    private static string Glyph(Sensor s) => s.Type switch
    {
        "TemperatureProvider" => "",
        "SpeedProvider" => "",
        "PowerProvider" => "",
        "VoltageProvider" => "",
        "ClockProvider" => "",
        "MemoryProvider" => "",
        "LatencyProvider" => "",
        "DataRateProvider" => s.Group.Contains("disk", StringComparison.OrdinalIgnoreCase) ? "" : "",
        _ => "",
    };

    private async Task Connect()
    {
        var pipe = _pipe = new WebPipe();
        pipe.Updated += OnUpdate;
        pipe.Failed += ex =>
        {
            EmptyContent = Ui.ErrorItem(ex, () => RaiseItemsChanged());
            _sensors = [];
            _pipe = null;
            RaiseItemsChanged();
        };
        try
        {
            await pipe.Connect(_api.Settings.WebPipeUrl);
        }
        catch (Exception ex)
        {
            EmptyContent = Ui.ErrorItem(ex, () => RaiseItemsChanged());
            _pipe = null;
            RaiseItemsChanged();
        }
    }

    private async Task DisconnectIfIdle()
    {
        if (_pipe is { } pipe && DateTime.UtcNow - _lastSeen > TimeSpan.FromSeconds(20))
        {
            _pipe = null;
            await pipe.DisposeAsync();
        }
    }

    private void ToggleFavorite(Sensor s)
    {
        if (!_favorites.Remove(s.Uid))
        {
            _favorites.Add(s.Uid);
        }

        File.WriteAllText(FavoritesFile, JsonSerializer.Serialize(_favorites));

        // the favorite action and star change: build these items again
        foreach (var key in _items.Keys.Where(k => k.EndsWith($"|{s.Uid}", StringComparison.Ordinal)).ToArray())
        {
            _items.Remove(key);
        }

        _layout = [];
        RaiseItemsChanged();
    }

    private SensorItem Fill(string key, Sensor s, string section, Sensor[] visible)
    {
        if (!_items.TryGetValue(key, out var item))
        {
            var favorite = _favorites.Contains(s.Uid);
            var uid = s.Uid;
            Sensor Current() => _sensors.FirstOrDefault(x => x.Uid == uid) ?? s;
            _items[key] = item = new SensorItem(() => _lastSeen = DateTime.UtcNow)
            {
                Title = s.Name,
                Subtitle = section == "Favorites" ? s.Group : string.Empty,
                Icon = Ui.Glyph(Glyph(s)),
                Section = section,
                Details = new Details(),
                MoreCommands =
                [
                    Ui.Action(favorite ? "Remove from Favorites" : "Add to Favorites", "", () => ToggleFavorite(Current()), Ui.CtrlShift(VirtualKey.P)),
                    CopyLater("Copy Sensor and Value", () => $"{Current().Group} {Current().Name}: {Current().Display}", Ui.CtrlShift((VirtualKey)0xBE)),
                    CopyLater("Copy All Shown Sensors", () => string.Join("\n\n", Rows().Visible.GroupBy(x => x.Group).Select(g => $"{g.Key}\n{string.Join("\n", g.Select(x => $"  {x.Name}: {x.Display}"))}"))),
                ],
            };
            item.Command = new CopyTextCommand(s.Display) { Name = "Copy Value" };
        }

        var history = new[] { ("Seconds", s.History.Seconds), ("Minutes", s.History.Minutes), ("5-minute steps", s.History.FiveMinutes), ("Hours", s.History.Hours) }
            .Where(h => h.Item2.Length > 0)
            .Select(h => $"**{h.Item1}** ({h.Item2.Length})\n```\n{Sparkline(h.Item2, 40)}\n```\nmin {WithUnit(h.Item2.Min(), s.Unit)} · avg {WithUnit(h.Item2.Average(), s.Unit)} · max {WithUnit(h.Item2.Max(), s.Unit)}");
        ((CopyTextCommand)item.Command!).Text = s.Display;
        item.Tags = _favorites.Contains(s.Uid) ? [Ui.Tag(Sparkline(s.History.Seconds, 20)), Ui.Yellow("★"), ValueTag(s)] : [Ui.Tag(Sparkline(s.History.Seconds, 20)), ValueTag(s)];
        var details = (Details)item.Details!;
        details.Title = s.Display;
        details.Body = $"{s.Group} · {s.Name}\n\n{string.Join("\n\n", history)}";
        details.Metadata =
        [
            Ui.Label("Sensor", s.Name),
            Ui.Label("Device", s.Group),
            Ui.Label("Value", s.Display),
            Ui.Label("Range", $"{WithUnit(s.Min, s.Unit)} – {WithUnit(s.Max, s.Unit)}"),
            Ui.Label("Type", s.Type.Replace("Provider", string.Empty, StringComparison.Ordinal)),
            Ui.Label("ID", s.Uid),
        ];
        return item;
    }

    private static CommandContextItem CopyLater(string title, Func<string> text, KeyChord? shortcut = null) => Ui.Action(title, "", () =>
    {
        ClipboardHelper.SetText(text());
        Status.Info("Copied");
    }, shortcut);

    /// <summary>Command Palette reads the tags of shown items after each update – that tells the page is still open.</summary>
    private sealed partial class SensorItem(Action seen) : ListItem
    {
        public override ITag[] Tags
        {
            get
            {
                seen();
                return base.Tags;
            }
            set => base.Tags = value;
        }
    }
}

internal sealed partial class EffectsPage : AsyncListPage
{
    private readonly SignalApi _api;

    public EffectsPage(SignalApi api)
    {
        _api = api;
        Id = "effects";
        Name = "Open";
        Title = "Lighting Effects";
        Icon = Ui.Glyph("");
        PlaceholderText = "Search effects …";
        ShowDetails = true;
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        var lightingTask = _api.GetLighting(token);
        var effects = await _api.Effects(token);
        var current = await lightingTask;
        Title = $"{(current.Enabled ? "On" : "Off")} · {current.GlobalBrightness}% · {current.Name}";
        var global = await GlobalActions(current);
        return new([.. effects.Select(e =>
        {
            var isCurrent = e.Name == current.Name;
            return new ListItem(new AnonymousCommand(() => Run($"Applying {e.Name}", () => _api.Apply(e))) { Name = "Apply Effect", Result = CommandResult.KeepOpen() })
            {
                Title = e.Name,
                Icon = Ui.Glyph(isCurrent ? "" : ""),
                Tags = isCurrent ? [Ui.Green(current.Enabled ? "Active" : "Paused")] : [],
                Details = new EffectDetails(_api, e),
                MoreCommands = [new CommandContextItem(new PresetsPage(_api, e, Reload)) { Title = "Show Presets", RequestedShortcut = Ui.Ctrl(VirtualKey.P) }, .. global],
            };
        })]);
    }

    private void Run(string working, Func<Task> action) => Status.Start($"{working} …", $"{working}: done", action, Reload);

    private async Task<IContextItem[]> GlobalActions(Lighting current)
    {
        var commands = new List<IContextItem>
        {
            Ui.Action("Next Effect", "", () => Run("Next effect", () => _api.Step("next")), Ui.Ctrl(VirtualKey.Right)),
            Ui.Action("Previous Effect", "", () => Run("Previous effect", () => _api.Step("previous")), Ui.Ctrl(VirtualKey.Left)),
            Ui.Action("Random Effect", "", () => Run("Random effect", () => _api.Step("shuffle")), Ui.CtrlShift(VirtualKey.R)),
            Ui.Action(current.Enabled ? "Disable Lighting" : "Enable Lighting", "", () => Run(current.Enabled ? "Lighting off" : "Lighting on", () => _api.SetEnabled(!current.Enabled)), Ui.CtrlShift(VirtualKey.T)),
            new CommandContextItem(new BrightnessPage(_api)) { Title = "Set Brightness", RequestedShortcut = Ui.CtrlShift(VirtualKey.B) },
        };
        try
        {
            var (layouts, active) = await _api.Layouts();
            if (layouts.Length > 0)
            {
                commands.Add(new CommandContextItem(new LayoutsPage(_api, layouts, active, Reload)) { Title = "Switch Layout", RequestedShortcut = Ui.CtrlShift(VirtualKey.L) });
            }
        }
        catch (ApiException)
        {
            // layouts need SignalRGB Pro
        }

        commands.Add(Ui.Action("Refresh", "", Reload, Ui.Ctrl(VirtualKey.R)));
        return [.. commands];
    }

    /// <summary>Loads description and preview image of an effect when Command Palette first shows it.</summary>
    private sealed partial class EffectDetails : Details
    {
        private readonly SignalApi _api;
        private readonly Effect _effect;
        private int _requested;

        public EffectDetails(SignalApi api, Effect effect)
        {
            _api = api;
            _effect = effect;
            Title = effect.Name;
            base.Body = "_Loading …_";
            Size = ContentSize.Medium;
        }

        public override string Body
        {
            get
            {
                if (Interlocked.Exchange(ref _requested, 1) == 0)
                {
                    _ = Load();
                }

                return base.Body;
            }
            set => base.Body = value;
        }

        private async Task Load()
        {
            try
            {
                var info = await _api.EffectInfo(_effect.Id);
                var image = info?["image"]?.GetValue<string>();
                Body = $"{(string.IsNullOrEmpty(image) ? string.Empty : $"![]({image}?--x-cmdpal-fit=fit)\n\n")}{info?["description"]?.GetValue<string>() ?? string.Empty}";
                var uses = new[] { ("uses_audio", "Audio"), ("uses_video", "Screen"), ("uses_input", "Input"), ("uses_meters", "Meters") }
                    .Where(f => info?[f.Item1]?.GetValue<bool>() == true).Select(f => (ITag)Ui.Tag(f.Item2)).ToArray();
                var meta = new List<IDetailsElement>();
                if (info?["publisher"]?.GetValue<string>() is { Length: > 0 } publisher)
                {
                    meta.Add(Ui.Label("Publisher", publisher));
                }

                if (uses.Length > 0)
                {
                    meta.Add(Ui.Tags("Uses", uses));
                }

                Metadata = [.. meta];
            }
            catch (Exception ex)
            {
                Body = $"_{ex.Message}_";
            }
        }
    }
}

internal sealed partial class PresetsPage : AsyncListPage
{
    private readonly SignalApi _api;
    private readonly Effect _effect;
    private readonly Action _applied;

    public PresetsPage(SignalApi api, Effect effect, Action applied)
    {
        _api = api;
        _effect = effect;
        _applied = applied;
        Name = "Show Presets";
        Title = $"{effect.Name} Presets";
        Icon = Ui.Glyph("");
        EmptyContent = Ui.EmptyItem("This effect has no presets", string.Empty, "");
    }

    protected override async Task<Batch> Load(int page, CancellationToken token) =>
        new([.. (await _api.Presets(_effect.Id, token)).Select(p => new ListItem(new AnonymousCommand(() => Status.Start($"Applying {p} …", $"{p} applied", async () =>
        {
            await _api.Apply(_effect);
            await _api.ApplyPreset(_effect.Id, p);
        }, _applied)) { Name = "Apply Preset", Result = CommandResult.KeepOpen() })
        {
            Title = p,
            Icon = Ui.Glyph(""),
        })]);
}

internal sealed partial class BrightnessPage : ListPage
{
    private readonly SignalApi _api;

    public BrightnessPage(SignalApi api)
    {
        _api = api;
        Id = "set-brightness";
        Name = "Set Brightness";
        Title = "Set Brightness";
        Icon = Ui.Glyph("");
        PlaceholderText = "Type a value (0–100) or pick one";
    }

    public override IListItem[] GetItems()
    {
        int[] steps = [100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 0];
        var typed = int.TryParse(SearchText.Trim().TrimEnd('%'), out var t) ? Math.Clamp(t, 0, 100) : -1;
        return [.. (typed >= 0 ? steps.Prepend(typed).Distinct() : steps).Select(v => new ListItem(new AnonymousCommand(() =>
            Status.Start($"Brightness {v}% …", $"☀️ Brightness {v}%", () => _api.SetBrightness(v))) { Name = "Set", Result = CommandResult.GoBack() })
        {
            Title = $"{v}%",
            Icon = Ui.Glyph(""),
        })];
    }
}

internal sealed partial class LayoutsPage(SignalApi api, string[] layouts, string current, Action changed) : ListPage
{
    public override IListItem[] GetItems() => [.. layouts.Select(l => new ListItem(new AnonymousCommand(() =>
        Status.Start($"Layout {l} …", $"Layout {l}", () => api.SetLayout(l), changed)) { Name = "Switch", Result = CommandResult.GoBack() })
    {
        Title = l,
        Icon = Ui.Glyph(l == current ? "" : ""),
    })];
}

internal sealed partial class AudioOutputPage : AsyncListPage
{
    private readonly Companion _companion;

    public AudioOutputPage(Companion companion)
    {
        _companion = companion;
        Id = "audio-output";
        Name = "Open";
        Title = "Switch Audio Output";
        Icon = Ui.Glyph("");
        PlaceholderText = "Search audio outputs …";
        MaxAge = TimeSpan.FromSeconds(3);
    }

    protected override async Task<Batch> Load(int page, CancellationToken token) =>
        new([.. (await _companion.AudioDevices()).Select(d =>
        {
            var (title, subtitle) = Companion.SplitDeviceName(d.Name);
            var headset = System.Text.RegularExpressions.Regex.IsMatch(d.Name, "kopfh|head|virtuoso|arctis|hyperx", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return new ListItem(new AnonymousCommand(() => Status.Start($"Switching to {title} …", $"🔊 {title}", () => _companion.SetAudioDevice(d.Id), Reload)) { Name = "Set as Default Output", Result = CommandResult.KeepOpen() })
            {
                Title = title,
                Subtitle = subtitle,
                Icon = Ui.Glyph(d.Default ? "" : headset ? "" : ""),
                Tags = d.Default ? [Ui.Green("Default")] : [],
            };
        })]);
}

/// <summary>Live status of the companion helper (polled every 2 s while the page is shown).</summary>
internal sealed partial class CompanionPage : ListPage
{
    private readonly Companion _companion;
    private JsonNode? _status;
    private bool _loaded;
    private DateTime _lastSeen;
    private Timer? _timer;

    public CompanionPage(Companion companion)
    {
        _companion = companion;
        Id = "companion";
        Name = "Open";
        Title = "Headset & Companion";
        Icon = Ui.Glyph("");
        IsLoading = true;
    }

    public override IListItem[] GetItems()
    {
        _lastSeen = DateTime.UtcNow;
        _timer ??= new Timer(_ => _ = Poll(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        if (!_loaded)
        {
            return [];
        }

        var service = ServiceActions(_status is not null);
        if (_status is null)
        {
            EmptyContent = new CommandItem(service[0] is CommandContextItem start ? start.Command! : new NoOpCommand())
            {
                Title = "Companion service is not running",
                Subtitle = _companion.Settings.ServiceEnabled ? "Enter starts it; it is kept running while Command Palette runs" : "Switch “Run the companion service” on in the settings",
                Icon = Ui.Glyph(""),
            };
            return [];
        }

        var items = new List<IListItem>();
        var h = _status["headset"];
        if (h is not null)
        {
            var name = $"{h["name"]}{(h["present"]?.GetValue<bool>() == true ? string.Empty : " · receiver not connected")}";
            var battery = h["battery"] as JsonValue;
            var low = h["batteryLow"]?.GetValue<bool>() == true;
            items.Add(new ListItem(new AnonymousCommand(() => Status.Start("Reading battery …", "Battery read", async () => Status.Info($"Battery {await _companion.ReadBattery()}%"))) { Name = "Read Battery Now", Result = CommandResult.KeepOpen() })
            {
                Title = battery is not null ? $"Battery {battery}%" : "Battery unknown",
                Subtitle = battery is not null ? $"read {Since(h["batteryAt"])}" : "Headset off or asleep",
                Icon = Ui.Glyph(""),
                Section = name,
                Tags = low ? [Ui.Red("Low")] : [],
                MoreCommands = [Ui.Action("Play Alert Sound", "", () => Status.Start("Playing sound …", "Sound played", _companion.TestSound)), .. service],
            });

            var muted = h["windowsMuted"]?.GetValue<bool>();
            var tags = new List<ITag>();
            if (h["listening"]?.GetValue<bool>() == false)
            {
                tags.Add(Ui.Orange("button not monitored"));
            }

            if (h["lastMuteChange"] is not null)
            {
                tags.Add(Ui.Tag($"{(h["lastMuteSource"]?.ToString() == "headset" ? "headset button" : "Windows")} · {Since(h["lastMuteChange"])}"));
            }

            items.Add(new ListItem(new AnonymousCommand(() => Status.Start("Microphone …", muted == true ? "🎤 Microphone live" : "🔇 Microphone muted", () => _companion.SetMic(muted == true ? "0" : "1"))) { Name = muted == true ? "Unmute Microphone" : "Mute Microphone", Result = CommandResult.KeepOpen() })
            {
                Title = muted is null ? "Microphone unknown" : muted.Value ? "Microphone muted" : "Microphone live",
                Subtitle = h["micName"]?.ToString() ?? string.Empty,
                Icon = Ui.Glyph(muted == true ? "" : ""),
                Section = name,
                Tags = [.. tags],
                MoreCommands = service,
            });
        }

        if (_status["keyboard"] is { } k)
        {
            var gameMode = k["gameMode"]?.GetValue<bool>() == true;
            var tags = new List<ITag>();
            if (k["lastError"]?.ToString() is { Length: > 0 } error)
            {
                tags.Add(Ui.Red(error));
            }

            if (k["reportedAt"] is not null)
            {
                tags.Add(Ui.Tag($"reported {Since(k["reportedAt"])}"));
            }

            items.Add(new ListItem(new NoOpCommand())
            {
                Title = $"Game Mode {(gameMode ? "on" : "off")}",
                Subtitle = k["gameActive"]?.GetValue<bool>() == true ? $"Game: {k["game"]}" : k["reportedAt"] is not null ? "No game running" : "Waiting for the Power Plan extension",
                Icon = Ui.Glyph(""),
                Section = $"{k["name"]}{(k["present"]?.GetValue<bool>() == true ? string.Empty : " · not connected")}",
                Tags = [.. tags],
                MoreCommands = service,
            });
        }

        if (_status["teamspeak"] is { } ts && ts["enabled"]?.GetValue<bool>() == true)
        {
            var state = ts["state"]?.ToString();
            var ready = TeamSpeakSetupPage.IsReady(ts);
            var servers = ts["servers"]?.GetValue<int>() ?? 0;
            var tsMuted = ts["inputMuted"]?.GetValue<bool>() == true;
            var title = state == "offline" ? "TeamSpeak not running" : state == "approval" ? "Waiting for approval in TeamSpeak" : servers == 0 ? "TeamSpeak connected, no server" : tsMuted ? "TeamSpeak microphone muted" : "TeamSpeak microphone live";
            var subtitle = !ready
                ? state == "offline" ? "Press Enter to set up"
                : state == "approval" ? "Allow the companion in TeamSpeak · Enter for help"
                : servers == 0 ? "Join a server to finish the setup · Enter for help"
                : ts["keyProblem"]?.ToString() == "switches-back" ? "⚠️ TeamSpeak unmutes again on key release · Enter to fix"
                : ts["keyBound"]?.GetValue<bool>() == false ? "⚠️ Mute key not working · Enter to fix"
                : "One step left: bind the mute key · Enter"
                : $"Follows the headset mute{(ts["nickname"] is { } nick ? $" · {nick}" : string.Empty)}";
            var setup = new CommandContextItem(new TeamSpeakSetupPage(_companion)) { Title = ready ? "TeamSpeak Setup" : "Configure TeamSpeak Mute" };
            ICommand primary = ready
                ? new AnonymousCommand(() => Status.Start("Pressing mute key …", "Mute key pressed", () => _companion.PressTeamSpeakKey(0))) { Name = "Toggle TeamSpeak Mute", Result = CommandResult.KeepOpen() }
                : setup.Command!;
            items.Add(new ListItem(primary)
            {
                Title = title,
                Subtitle = subtitle,
                Icon = Ui.Glyph(state != "connected" ? "" : tsMuted ? "" : ""),
                Section = "TeamSpeak",
                Tags = ready ? [Ui.Green("set up")] : [],
                MoreCommands = ready ? [setup, .. service] : service,
            });
        }

        if (_status["cs2"] is { } c)
        {
            items.AddRange(Cs2Items(c, service));
        }

        items.Add(new ListItem(new CompanionLogPage(_companion))
        {
            Title = "Companion service running",
            Subtitle = $"v{_status["version"]} · pid {_status["pid"]} · since {(DateTime.TryParse(_status["started"]?.ToString(), out var started) ? started.ToString("g") : "?")}",
            Icon = Ui.Glyph(""),
            Section = "Service",
            MoreCommands = service,
        });
        return [.. items];
    }

    private static string Since(JsonNode? iso)
    {
        if (!DateTimeOffset.TryParse(iso?.ToString(), out var t))
        {
            return string.Empty;
        }

        var s = (DateTimeOffset.Now - t).TotalSeconds;
        return s < 60 ? $"{s:0}s ago" : s < 3600 ? $"{s / 60:0} min ago" : $"{s / 3600:0} h ago";
    }

    private async Task Poll()
    {
        if (DateTime.UtcNow - _lastSeen > TimeSpan.FromSeconds(15))
        {
            _timer?.Dispose();
            _timer = null;
            return;
        }

        _status = await _companion.Status();
        _loaded = true;
        IsLoading = false;
        RaiseItemsChanged();
    }

    private IContextItem[] ServiceActions(bool running)
    {
        var list = new List<IContextItem>();
        if (running)
        {
            list.Add(Ui.Action("Restart Service", "", () => Status.Start("Restarting …", "Companion service restarted", _companion.Restart)));
            list.Add(Ui.Action("Stop Service", "", () => Status.Start("Stopping …", "Companion service stopped", _companion.Stop)));
        }
        else
        {
            list.Add(Ui.Action("Start Service", "", () => Status.Start("Starting companion service …", "Companion service running", _companion.Start)));
        }

        list.Add(new CommandContextItem(new CompanionLogPage(_companion)) { Title = "Show Log", RequestedShortcut = Ui.Ctrl(VirtualKey.L) });
        list.Add(new CommandContextItem(Ui.OpenSettings(SignalRgbProvider.ProviderId)) { Title = "Companion Settings" });
        return [.. list];
    }

    private IEnumerable<ListItem> Cs2Items(JsonNode c, IContextItem[] service)
    {
        var st = c["state"];
        var listening = c["listening"]?.GetValue<bool>() == true;
        string title = "CS2 not running", subtitle = "Servers start automatically when cs2.exe runs", glyph = "";
        if (c["enabled"]?.GetValue<bool>() == false)
        {
            (title, subtitle) = ("CS2 integration off", "Enable it in the SignalRGB settings");
        }
        else if (listening && st?["connected"]?.GetValue<bool>() == true)
        {
            title = $"{st["health"] ?? "?"} HP · {st["armor"] ?? 0} armor · {st["team"] ?? "–"}";
            subtitle = $"Round {st["round"] ?? "–"} {st["round_phase"]} · {st["kills"] ?? 0}/{st["deaths"] ?? 0} K/D{(st["weapon"] is { } w ? $" · {w.ToString().Replace("weapon_", string.Empty, StringComparison.Ordinal)}" : string.Empty)}";
            glyph = "";
        }
        else if (listening)
        {
            title = "CS2 running · waiting for game data";
            subtitle = (c["posts"]?.GetValue<int>() ?? 0) > 0 ? $"last update {Since(c["lastPostAt"])}" : "No data yet (is the GSI config installed?)";
            glyph = "";
        }
        else if (c["gameRunning"]?.GetValue<bool>() == true || c["error"] is not null)
        {
            (title, subtitle, glyph) = ("CS2 running · servers not started", c["error"]?.ToString() ?? string.Empty, "");
        }

        var port = c["port"]?.GetValue<int>() ?? 3000;
        yield return new ListItem(listening ? new OpenUrlCommand($"http://127.0.0.1:{port}/state") { Name = "Open Game State" } : new NoOpCommand())
        {
            Title = title,
            Subtitle = subtitle,
            Icon = Ui.Glyph(glyph),
            Section = "Counter-Strike 2",
            Tags = listening ? [Ui.Green($":{port} · :{c["httpsPort"]}")] : [],
            MoreCommands = service,
        };

        var cfg = Companion.Cs2CfgInstalled();
        var install = new AnonymousCommand(() =>
        {
            try
            {
                Status.Info($"GSI config installed: {Companion.InstallCs2Cfg(port, "signalcompanion")} – restart CS2");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Status.Error(ex.Message);
            }

            RaiseItemsChanged();
        })
        { Name = cfg is null ? "Install GSI Config" : "Reinstall GSI Config", Result = CommandResult.KeepOpen() };
        yield return new ListItem(install)
        {
            Title = cfg is null ? "GSI config not installed" : "GSI config installed",
            Subtitle = cfg ?? "CS2 needs it to send game data; restart CS2 afterwards",
            Icon = Ui.Glyph(cfg is null ? "" : ""),
            Section = "Counter-Strike 2",
            MoreCommands = cfg is null ? [] : [Ui.Action("Remove GSI Config", "", () =>
            {
                Companion.UninstallCs2Cfg();
                RaiseItemsChanged();
            })],
        };

        var effect = Companion.EffectInstalled();
        var trusted = c["caTrusted"] is JsonArray { Count: > 0 };
        yield return new ListItem(new AnonymousCommand(() =>
        {
            try
            {
                Status.Info($"Effect installed: {Companion.InstallEffect()}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status.Error(ex.Message);
            }

            RaiseItemsChanged();
        })
        { Name = effect ? "Reinstall Effect" : "Install Effect", Result = CommandResult.KeepOpen() })
        {
            Title = effect ? "CS2 Reactive effect installed" : "CS2 Reactive effect not installed",
            Subtitle = c["caPatchedNow"]?.GetValue<bool>() == true ? "Restart SignalRGB once so it trusts the HTTPS bridge"
                : trusted ? "SignalRGB trusts the HTTPS bridge"
                : c["httpsRunning"]?.GetValue<bool>() == true ? "⚠️ SignalRGB doesn't trust the HTTPS bridge, the effect gets no data (see Show Log)"
                : "Apply “CS2 Reactive (SignalCompanion)” to a layer in SignalRGB",
            Icon = Ui.Glyph(effect ? "" : ""),
            Section = "Counter-Strike 2",
        };
    }
}

internal sealed partial class CompanionLogPage(Companion companion) : AsyncMarkdownPage
{
    protected override async Task<(string Markdown, IDetails? Details, IContextItem[] Commands)> Load(CancellationToken token)
    {
        Name = "Show Log";
        Title = "Companion Service Log";
        var lines = await companion.Log(300);
        return (lines.Length == 0 ? "_No log yet_" : $"```\n{string.Join("\n", lines.Reverse())}\n```", null, [Ui.Copy(string.Join("\n", lines), "Copy Log", Ui.CtrlShift(VirtualKey.C)), Ui.Action("Refresh", "", Refresh, Ui.Ctrl(VirtualKey.R))]);
    }
}

/// <summary>Guided TeamSpeak setup with live check marks (polled every 1.5 s while shown).</summary>
internal sealed partial class TeamSpeakSetupPage : ListPage
{
    private readonly Companion _companion;
    private JsonNode? _ts;
    private bool _loaded;
    private DateTime _lastSeen;
    private Timer? _timer;

    public TeamSpeakSetupPage(Companion companion)
    {
        _companion = companion;
        Name = "Configure TeamSpeak Mute";
        Title = "Configure TeamSpeak Mute";
        Icon = Ui.Glyph("");
        ShowDetails = true;
        IsLoading = true;
    }

    public static bool IsReady(JsonNode? ts) => ts?["state"]?.ToString() == "connected" && ts["keyBound"]?.GetValue<bool>() == true;

    public override IListItem[] GetItems()
    {
        _lastSeen = DateTime.UtcNow;
        _timer ??= new Timer(_ => _ = Poll(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1.5));
        if (!_loaded)
        {
            return [];
        }

        if (_ts is null)
        {
            EmptyContent = Ui.EmptyItem("Companion service is not running", "Start it in Headset & Companion first", "");
            return [];
        }

        if (_ts["enabled"]?.GetValue<bool>() != true)
        {
            EmptyContent = new CommandItem(Ui.OpenSettings(SignalRgbProvider.ProviderId)) { Title = "TeamSpeak integration is off", Subtitle = "Enable “Headset mute also mutes TeamSpeak” in the settings", Icon = Ui.Glyph("") };
            return [];
        }

        var state = _ts["state"]?.ToString();
        var running = state is not null && state != "offline";
        var approved = state == "connected";
        var onServer = approved && (_ts["servers"]?.GetValue<int>() ?? 0) > 0;
        var keyBound = _ts["keyBound"]?.GetValue<bool>();
        var bound = keyBound == true;
        var problem = _ts["keyProblem"]?.ToString();

        var send = Ui.Action("Send Key in 5 Seconds", "", SendKey);
        var test = Ui.Action("Test Mute Key", "", () => _ = Test());
        return
        [
            Step("1. TeamSpeak is running", running ? "done" : "todo", running ? string.Empty : _ts["lastError"]?.ToString() ?? string.Empty, """
## 1. Start TeamSpeak
Start **TeamSpeak 5 or 6**. The companion connects to it automatically through TeamSpeak's *Remote Apps* interface.

If this step stays open while TeamSpeak runs: **TeamSpeak → Settings → Remote Apps** must be enabled (it is by default).
""", []),
            Step("2. Allow the companion in TeamSpeak", approved ? "done" : running ? "waiting" : "todo", running && !approved ? "TeamSpeak is asking you right now" : string.Empty, """
## 2. Allow the companion
TeamSpeak asks once whether **"SignalRGB Companion"** may connect. Click **Allow**.

Don't see the question? Bring the TeamSpeak window to the front, or look under **Settings → Remote Apps**, where pending apps are listed. The approval is stored, you won't be asked again.
""", []),
            Step("3. Connect to a TeamSpeak server", onServer ? "done" : approved ? "waiting" : "todo", string.Empty, """
## 3. Connect to a server
Join any TeamSpeak server. The companion needs a connection to read your microphone mute state, and to check in the next steps that the key works.
""", []),
            Step("4. Bind the mute key in TeamSpeak", bound ? "done" : keyBound == false ? "failed" : "todo",
                problem == "switches-back" ? "TeamSpeak switches back on key release: wrong action or bound twice" : keyBound == false ? "Last key press didn't mute TeamSpeak" : string.Empty, """
## 4. Bind the mute key
TeamSpeak can't be muted directly by other apps. Instead, the companion presses a *virtual key* that you assign to TeamSpeak's mute action once:

1. In TeamSpeak open **Settings → Key Bindings** and add a binding for **Toggle Microphone Mute** (*Mikrofon stummschalten umschalten*). Don't start recording yet.
2. Here run **Send Key in 5 Seconds** (Enter). Command Palette closes.
3. Within those 5 seconds click **record** in TeamSpeak and keep the TeamSpeak window in front.

TeamSpeak then shows the key as *signalrgb.mic.toggle*. Save the binding, then run step 5.

**Mute switches straight back off?** Then the key is bound to an action that unmutes on release (e.g. *Push-to-Mute*) or it is bound twice. In **Key Bindings**, delete every binding that uses *signalrgb.mic.toggle* and bind it **once**, only to **Toggle Microphone Mute**. Until the test in step 5 passes, the companion doesn't press the key, so your headset mute always stays as you set it.
""", [send, test]),
            Step("5. Test", bound ? "done" : keyBound == false ? "failed" : "todo", bound ? "Headset mute now mutes TeamSpeak" : string.Empty, """
## 5. Test
**Test Mute Key** (Enter) presses the key once, checks that TeamSpeak's microphone mute changed, and switches it back.

When this step is green you're done: the headset mute button now also mutes TeamSpeak, and muting in TeamSpeak mutes the headset and Windows.
""", [test]),
        ];
    }

    private static ListItem Step(string title, string state, string subtitle, string markdown, IContextItem[] actions) =>
        new(actions.FirstOrDefault() is CommandContextItem first ? first.Command! : new NoOpCommand())
        {
            Title = title,
            Subtitle = subtitle,
            Icon = Ui.Glyph(state switch { "done" => "", "waiting" => "", "failed" => "", _ => "" }),
            Tags = state switch { "done" => [Ui.Green("done")], "waiting" => [Ui.Orange("waiting")], "failed" => [Ui.Red("failed")], _ => [] },
            Details = new Details { Body = markdown },
            MoreCommands = [.. actions.Skip(1)],
        };

    private async Task Poll()
    {
        if (DateTime.UtcNow - _lastSeen > TimeSpan.FromSeconds(15))
        {
            _timer?.Dispose();
            _timer = null;
            return;
        }

        _ts = (await _companion.Status())?["teamspeak"];
        _loaded = true;
        IsLoading = false;
        RaiseItemsChanged();
    }

    private void SendKey() => _ = Task.Run(async () =>
    {
        try
        {
            await _companion.PressTeamSpeakKey(5);
            Status.Info("Click “record” in TeamSpeak now, the key comes in 5 seconds");
        }
        catch (Exception ex)
        {
            Status.Error($"TeamSpeak not ready: {ex.Message}");
        }
    });

    private async Task Test()
    {
        try
        {
            await _companion.TestTeamSpeakKey();

            // the companion reports the result within ~2 s (keyBound)
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(250);
                var now = (await _companion.Status())?["teamspeak"];
                if (now?["keyBound"]?.GetValue<bool>() == true)
                {
                    await Task.Delay(600);
                    await _companion.TestTeamSpeakKey(); // switch back
                    Status.Info("Works: TeamSpeak follows the headset mute");
                    return;
                }

                if (now?["keyBound"]?.GetValue<bool>() == false)
                {
                    break;
                }
            }

            Status.Error("TeamSpeak didn't react – bind the key to “Toggle Microphone Mute” (step 4)");
        }
        catch (Exception ex)
        {
            Status.Error($"Test failed: {ex.Message}");
        }
    }
}
