using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.PowerPlan;

public sealed class PowerPlanSettings : SettingsBase
{
    private readonly ToggleSetting _fullscreen = new("detectFullscreen", "Treat fullscreen apps as games", "Also count any fullscreen app in the foreground as a game (browsers and video players are ignored)", false);
    private readonly TextSetting _libraries = new("extraLibraries", "Additional game folders", "Folders whose programs count as games, separated by ; (e.g. D:\\Games). Steam, Epic, GOG, Xbox, Ubisoft, EA, Riot, Battle.net and Amazon libraries are detected automatically.", string.Empty);
    private readonly TextSetting _cooldown = new("cooldown", "Switch back after (seconds)", "Seconds to wait after the last game closed before switching to the idle plan", "60");
    private readonly ChoiceSetSetting _notify = new("notificationStyle", "Notify on switch", "Show a Windows notification when the plan changes automatically", Choices(("Off", "none"), ("Windows notification", "system")));
    private readonly ToggleSetting _enforce = new("enforce", "Restore the plan when other programs change it", "On (recommended): if a game or tool switches the plan, it is restored within about 10 seconds. To pick a plan yourself, activate it in “Power Plans” (pauses the automation for 1 hour).", true);

    public PowerPlanSettings()
        : base("power-plan")
    {
        Settings.Add(_fullscreen);
        Settings.Add(_libraries);
        Settings.Add(_cooldown);
        Settings.Add(_notify);
        Settings.Add(_enforce);
        Load();
    }

    public bool DetectFullscreen => _fullscreen.Value;

    public TimeSpan Cooldown => TimeSpan.FromSeconds(int.TryParse(_cooldown.Value, out var s) ? Math.Max(0, s) : 60);

    public bool Notify => _notify.Value == "system";

    public bool Enforce => _enforce.Value;

    public string[] ExtraLibraries => [.. (_libraries.Value ?? string.Empty)
        .Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => p.ToLowerInvariant().Replace('/', '\\').TrimEnd('\\') + "\\")
        .Where(p => p.Length > 2)];
}

/// <summary>Rules the user set in the lists (not on the settings page).</summary>
public sealed class Rules
{
    public bool Enabled { get; set; }

    public string? GamePlan { get; set; }

    public string? IdlePlan { get; set; }

    public List<string> CustomGames { get; set; } = [];

    public List<string> Excluded { get; set; } = [];

    public DateTime? PausedUntil { get; set; }

    public bool Paused => PausedUntil > DateTime.Now;
}

public sealed record Activity(DateTime Time, string Kind, string Text, string? Detail);

public sealed record Detection(Proc Process, string Reason, string Detail)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Process.Name);
}

/// <summary>
/// Switches to the game plan while a game runs and back to the idle plan afterwards. Runs every
/// 10 seconds inside the extension process. Also tells the SignalRGB companion helper whether a game runs.
/// </summary>
public sealed class PowerAutomation : IDisposable
{
    // Path fragments of game libraries; matched case-insensitively against the exe path
    private static readonly string[] LibraryPaths =
    [
        @"\steamapps\common\", @"\epic games\", @"\gog galaxy\games\", @"\gog games\", @"\xboxgames\", @"\riot games\",
        @"\ubisoft game launcher\games\", @"\ea games\", @"\origin games\", @"\battle.net\games\", @"\amazon games\library\",
    ];

    // Launchers, helpers and tools that live in game folders but aren't games
    private static readonly string[] BuiltinExcludes =
    [
        "steam.exe", "steamwebhelper.exe", "steamservice.exe", "steamerrorreporter.exe", "steamerrorreporter64.exe", "gameoverlayui.exe",
        "gameoverlayui64.exe", "epicgameslauncher.exe", "epicwebhelper.exe", "epiconlineservices*.exe", "eosoverlayrenderer*.exe",
        "unrealcefsubprocess.exe", "crashreportclient.exe", "easyanticheat*.exe", "start_protected_game.exe", "beservice*.exe", "battleye*.exe",
        "riotclientservices.exe", "riotclientux*.exe", "riotclientcrashhandler.exe", "vgtray.exe", "galaxyclient*.exe", "gogalaxy*.exe", "upc.exe",
        "ubisoftconnect.exe", "uplaywebcore.exe", "eadesktop.exe", "eabackgroundservice.exe", "battle.net*.exe", "agent.exe", "amazon games*.exe",
        "wallpaper32.exe", "wallpaper64.exe", "webwallpaper32.exe", "ui32.exe", "cefsharp.browsersubprocess.exe", "crashpad_handler.exe",
        "unitycrashhandler*.exe", "vc_redist*.exe", "dxsetup.exe",
    ];

    // Apps that are often fullscreen but aren't games (only used for fullscreen detection)
    private static readonly string[] NotGamesFullscreen =
    [
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "vlc.exe", "mpc-hc*.exe", "mpv.exe", "potplayer*.exe",
        "explorer.exe", "applicationframehost.exe", "raycast.exe", "code.exe", "powerpnt.exe", "teams.exe", "ms-teams.exe", "zoom.exe", "jellyfin*.exe",
        "plex*.exe", "netflix*.exe", "searchhost.exe", "lockapp.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe", "textinputhost.exe",
        "microsoft.cmdpal.ui.exe",
    ];

    private readonly string _rulesFile = Path.Combine(Paths.For("power-plan"), "rules.json");
    private readonly string _activityFile = Path.Combine(Paths.For("power-plan"), "activity.json");
    private readonly Lock _gate = new();
    private Timer? _timer;
    private string _mode = string.Empty;
    private DateTime _lastGameSeen;
    private string? _lastGame;
    private string? _lastActive;

    public PowerAutomation(PowerPlanSettings settings)
    {
        Settings = settings;
        Rules = LoadRules();
        _timer = new Timer(_ => Tick(manual: false), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
    }

    public event Action<string>? StatusChanged;

    public PowerPlanSettings Settings { get; }

    public Rules Rules { get; }

    public string Status { get; private set; } = "…";

    public static string GamePlanOf(Rules rules, Plan[] plans) =>
        new[] { rules.GamePlan, PowerApi.HighPerformance, PowerApi.Ultimate, PowerApi.Balanced }.FirstOrDefault(g => g is not null && plans.Any(p => p.Guid == g)) ?? string.Empty;

    public static string IdlePlanOf(Rules rules, Plan[] plans) =>
        new[] { rules.IdlePlan, PowerApi.PowerSaver, PowerApi.Balanced }.FirstOrDefault(g => g is not null && plans.Any(p => p.Guid == g)) ?? string.Empty;

    public static bool Matches(string name, IEnumerable<string> patterns)
    {
        var lower = name.ToLowerInvariant();
        return patterns.Any(p => p.Contains('*', StringComparison.Ordinal)
            ? Regex.IsMatch(lower, $"^{Regex.Escape(p).Replace("\\*", ".*", StringComparison.Ordinal)}$")
            : p == lower);
    }

    public string? LibraryOf(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var lower = path.ToLowerInvariant();
        return LibraryPaths.FirstOrDefault(f => lower.Contains(f, StringComparison.Ordinal)) ?? Settings.ExtraLibraries.FirstOrDefault(lower.StartsWith);
    }

    public bool IsExcluded(string name) => Matches(name, Rules.Excluded) || Matches(name, BuiltinExcludes);

    /// <summary>Running games, one entry per exe name.</summary>
    public List<Detection> DetectGames(Proc[] processes, Foreground? fg)
    {
        var found = new Dictionary<string, Detection>();
        foreach (var p in processes)
        {
            var key = p.Name.ToLowerInvariant();
            if (found.ContainsKey(key) || Matches(key, Rules.Excluded))
            {
                continue;
            }

            if (Matches(key, Rules.CustomGames))
            {
                found[key] = new Detection(p, "custom", "On your game list");
                continue;
            }

            if (!Matches(key, BuiltinExcludes) && LibraryOf(p.Path) is { } library)
            {
                found[key] = new Detection(p, "library", $"In game library ({library.Replace('\\', ' ').Trim()})");
            }
        }

        if (Settings.DetectFullscreen && fg is { Fullscreen: true, Name: { } name })
        {
            var key = name.ToLowerInvariant();
            if (!found.ContainsKey(key) && !IsExcluded(key) && !Matches(key, NotGamesFullscreen))
            {
                found[key] = new Detection(new Proc(fg.Pid, name, fg.Path), "fullscreen", $"Fullscreen in the foreground{(fg.Title.Length > 0 ? $": {fg.Title}" : string.Empty)}");
            }
        }

        return [.. found.Values];
    }

    public void SaveRules(bool resetState = true)
    {
        lock (_gate)
        {
            File.WriteAllText(_rulesFile, JsonSerializer.Serialize(Rules));
            if (resetState)
            {
                // apply the new setup on the next run
                _mode = string.Empty;
            }
        }

        Tick(manual: false);
    }

    public Activity[] ActivityLog()
    {
        try
        {
            return File.Exists(_activityFile) ? JsonSerializer.Deserialize<Activity[]>(File.ReadAllText(_activityFile)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    public void AddActivity(string kind, string text, string? detail = null)
    {
        lock (_gate)
        {
            var list = ActivityLog().ToList();
            list.Insert(0, new Activity(DateTime.Now, kind, text, detail));
            File.WriteAllText(_activityFile, JsonSerializer.Serialize(list.Take(50)));
        }
    }

    public void ClearActivity() => File.Delete(_activityFile);

    /// <summary>A plan picked by hand would be undone by the automation, so pause it for an hour.</summary>
    public void ActivateByHand(Plan plan)
    {
        PowerApi.SetActive(plan.Guid);
        if (Rules.Enabled && !Rules.Paused)
        {
            Rules.PausedUntil = DateTime.Now.AddHours(1);
            AddActivity("manual", $"{plan.Name} chosen by hand", "Automatic switching paused for 1 hour");
            SaveRules();
            Core.Status.Info($"{plan.Name} active – automatic switching paused for 1 hour");
        }
        else
        {
            AddActivity("manual", $"{plan.Name} chosen by hand");
            Core.Status.Info($"{plan.Name} active");
        }
    }

    public void Tick(bool manual)
    {
        try
        {
            lock (_gate)
            {
                Run(manual);
            }
        }
        catch (Exception ex)
        {
            Log.Error("power-plan tick", ex);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private Rules LoadRules()
    {
        try
        {
            if (File.Exists(_rulesFile) && JsonSerializer.Deserialize<Rules>(File.ReadAllText(_rulesFile)) is { } rules)
            {
                return rules;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Error("power-plan rules", ex);
        }

        // off until switched on, so it never fights the Raycast extension unasked
        return new Rules { Enabled = false };
    }

    private void Run(bool manual)
    {
        var plans = PowerApi.Plans();
        var active = PowerApi.Active();
        var games = DetectGames(PowerApi.Processes(), PowerApi.GetForeground());
        ReportToCompanion(games.FirstOrDefault()?.Name);
        string Name(string? guid) => PowerApi.NameOf(plans, guid);

        if (!Rules.Enabled)
        {
            SetStatus($"Off · {Name(active)}");
            return;
        }

        if (Rules.Paused)
        {
            SetStatus($"Paused until {Rules.PausedUntil:HH:mm} · {Name(active)}");
            return;
        }

        if (_lastActive is not null && active != _lastActive)
        {
            AddActivity("external", $"Plan changed by another program: {Name(active)}", $"was {Name(_lastActive)}");
        }

        string mode;
        if (games.Count > 0)
        {
            mode = "game";
            _lastGameSeen = DateTime.UtcNow;
            var game = games[0];
            if (_mode != "game" || _lastGame != game.Name)
            {
                AddActivity("game", $"Game detected: {game.Name}", $"{game.Detail} · {game.Process.Path ?? game.Process.Name}");
            }

            _lastGame = game.Name;
        }
        else if (_mode == "game" && DateTime.UtcNow - _lastGameSeen < Settings.Cooldown)
        {
            // game just closed: wait a bit in case it restarts or a launcher hands over
            mode = "game";
        }
        else
        {
            mode = "idle";
            if (_mode == "game")
            {
                AddActivity("idle", "No game running anymore", $"{_lastGame ?? "Game"} closed");
            }
        }

        var target = mode == "game" ? GamePlanOf(Rules, plans) : IdlePlanOf(Rules, plans);
        var changed = mode != _mode;
        if (target.Length > 0 && target != active && (changed || Settings.Enforce || manual))
        {
            try
            {
                PowerApi.SetActive(target);
                AddActivity(changed || manual ? "switch" : "restore", changed || manual ? $"Switched to {Name(target)}" : $"Restored {Name(target)}", $"was {Name(active)}");
                active = target;
                if (Settings.Notify)
                {
                    Notify.Show(mode == "game" ? $"🎮 {_lastGame ?? "Game"} started" : "💤 No game running", $"Power plan: {Name(target)}", group: "power-plan");
                }
            }
            catch (InvalidOperationException ex)
            {
                AddActivity("error", $"Could not switch to {Name(target)}", ex.Message);
            }
        }

        _mode = mode;
        _lastActive = active;
        SetStatus(mode == "game" ? $"🎮 {_lastGame} · {Name(active)}" : $"💤 {Name(active)}");
        if (manual)
        {
            Core.Status.Info(Status);
        }
    }

    private void SetStatus(string status)
    {
        if (status != Status)
        {
            Status = status;
            StatusChanged?.Invoke(status);
        }
    }

    /// <summary>The SignalRGB companion switches the keyboard game mode; best effort.</summary>
    private static void ReportToCompanion(string? game)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await Http.Client().PostAsync("http://127.0.0.1:3010/api/game", JsonContent.Create(new { active = game is not null, name = game ?? string.Empty }), cts.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // helper not running
            }
        });
    }
}
