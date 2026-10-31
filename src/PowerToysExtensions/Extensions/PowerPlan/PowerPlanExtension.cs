using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.PowerPlan;

[Guid("8B555E16-BD45-431F-8AFF-8CD8B56BD750")]
public sealed partial class PowerPlanExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new PowerPlanProvider();
}

public sealed partial class PowerPlanProvider : CommandProvider
{
    private readonly PowerAutomation _auto;
    private readonly CommandItem _plans;
    private readonly CommandItem _toggle;
    private readonly CommandItem _band;

    public PowerPlanProvider()
    {
        var settings = new PowerPlanSettings();
        _auto = new PowerAutomation(settings);
        Id = "delido.power-plan";
        DisplayName = "Power Plan";
        Icon = Ui.Glyph("");
        Settings = settings.Settings;

        _plans = new CommandItem(new PowerPlansPage(_auto)) { Title = "Power Plans" };
        _toggle = new CommandItem(new AnonymousCommand(Toggle) { Id = "toggle-auto", Name = "Toggle", Result = CommandResult.KeepOpen() })
        {
            Title = "Toggle Automatic Power Plan",
            Icon = Ui.Glyph(""),
        };
        _band = new CommandItem(new PowerPlansPage(_auto) { Id = "power-plan-band" }) { Title = "Power" };
        _auto.StatusChanged += Update;
        Update(_auto.Status);
    }

    public override ICommandItem[] TopLevelCommands() => [_plans, new CommandItem(new GameDetectionPage(_auto)) { Title = "Game Detection", Subtitle = "Power Plan" }, _toggle];

    public override ICommandItem[]? GetDockBands() => [_band];

    public override ICommandItem? GetCommandItem(string id) => TopLevelCommands().Concat([_band]).FirstOrDefault(c => c.Command?.Id == id);

    public override void Dispose()
    {
        _auto.Dispose();
        base.Dispose();
    }

    private void Update(string status)
    {
        _plans.Subtitle = status;
        _band.Subtitle = status;
        _toggle.Subtitle = _auto.Rules.Enabled ? "On – switch off" : "Off – switch on";
    }

    private void Toggle()
    {
        _auto.Rules.Enabled = !_auto.Rules.Enabled;
        _auto.Rules.PausedUntil = null;
        _auto.SaveRules();
        Status.Info(_auto.Rules.Enabled ? "Automatic power plan is on" : "Automatic power plan is off");
        Update(_auto.Status);
    }
}

internal sealed partial class PowerPlansPage : AsyncListPage
{
    private static readonly (string Title, TimeSpan? For)[] PauseOptions =
        [("30 Minutes", TimeSpan.FromMinutes(30)), ("1 Hour", TimeSpan.FromHours(1)), ("3 Hours", TimeSpan.FromHours(3)), ("Until Tomorrow", null)];

    private readonly PowerAutomation _auto;

    public PowerPlansPage(PowerAutomation auto)
    {
        _auto = auto;
        Id = "power-plans";
        Name = "Open";
        Title = "Power Plans";
        Icon = Ui.Glyph("");
        PlaceholderText = "Filter power plans …";
        MaxAge = TimeSpan.FromSeconds(3);
    }

    protected override Task<Batch> Load(int page, CancellationToken token) => Task.Run(() =>
    {
        var plans = PowerApi.Plans();
        var active = PowerApi.Active();
        var rules = _auto.Rules;
        var games = _auto.DetectGames(PowerApi.Processes(), PowerApi.GetForeground());
        var gamePlan = PowerAutomation.GamePlanOf(rules, plans);
        var idlePlan = PowerAutomation.IdlePlanOf(rules, plans);
        var automation = AutomationActions().ToArray();
        var items = new List<IListItem>();

        string title, subtitle = string.Empty, glyph;
        if (!rules.Enabled)
        {
            (title, glyph) = ("Automatic switching is off", "");
        }
        else if (rules.Paused)
        {
            (title, subtitle, glyph) = ("Automatic switching is paused", $"until {rules.PausedUntil:ddd HH:mm}", "");
        }
        else
        {
            var game = games.FirstOrDefault();
            (title, subtitle, glyph) = game is null ? ("No game running", string.Empty, "") : ($"Game running: {game.Name}", game.Detail, "");
        }

        items.Add(new ListItem(automation[0].Command!)
        {
            Title = title,
            Subtitle = subtitle,
            Icon = Ui.Glyph(glyph),
            Section = "Automatic",
            Tags = rules.Enabled && !rules.Paused ? [Ui.Purple($"🎮 {PowerApi.NameOf(plans, gamePlan)}"), Ui.Blue($"💤 {PowerApi.NameOf(plans, idlePlan)}")] : [],
            MoreCommands = automation[1..],
        });

        foreach (var plan in plans)
        {
            var tags = new List<ITag>();
            if (plan.Guid == gamePlan)
            {
                tags.Add(Ui.Purple("Games"));
            }

            if (plan.Guid == idlePlan)
            {
                tags.Add(Ui.Blue("Otherwise"));
            }

            if (plan.Guid == active)
            {
                tags.Add(Ui.Green("Active"));
            }

            items.Add(new ListItem(new AnonymousCommand(() =>
            {
                try
                {
                    _auto.ActivateByHand(plan);
                }
                catch (InvalidOperationException ex)
                {
                    Status.Error($"Could not switch power plan: {ex.Message}");
                }

                Reload();
            })
            { Name = "Activate", Result = CommandResult.KeepOpen() })
            {
                Title = plan.Name,
                Icon = Ui.Glyph(plan.Guid switch { PowerApi.PowerSaver => "", PowerApi.Balanced => "", PowerApi.HighPerformance or PowerApi.Ultimate => "", _ => "" }),
                Section = "Power Plans",
                Tags = [.. tags],
                MoreCommands =
                [
                    Ui.Action("Use While Gaming", "", () => Change($"{plan.Name} is used while gaming", r => r.GamePlan = plan.Guid), Ui.CtrlShift(VirtualKey.G)),
                    Ui.Action("Use When No Game Runs", "", () => Change($"{plan.Name} is used when no game runs", r => r.IdlePlan = plan.Guid), Ui.CtrlShift(VirtualKey.I)),
                    .. automation,
                    Ui.Copy(plan.Guid, "Copy GUID", Ui.CtrlShift(VirtualKey.C)),
                ],
            });
        }

        var log = _auto.ActivityLog();
        var logText = string.Join("\n", log.Select(a => $"{a.Time:g}  {a.Text}{(a.Detail is null ? string.Empty : $" ({a.Detail})")}"));
        foreach (var entry in log.Take(20))
        {
            items.Add(new ListItem(new CopyTextCommand(logText) { Name = "Copy Activity Log" })
            {
                Title = entry.Text,
                Subtitle = entry.Detail ?? string.Empty,
                Icon = Ui.Glyph(entry.Kind switch { "game" => "", "idle" => "", "switch" => "", "restore" => "", "external" => "", "manual" => "", _ => "" }),
                Section = "Recent Activity",
                Tags = [Ui.Tag(Ui.Ago(entry.Time))],
                MoreCommands = [Ui.Action("Clear Activity Log", "", () =>
                {
                    _auto.ClearActivity();
                    Reload();
                }), .. automation],
            });
        }

        return new Batch([.. items]);
    });

    private void Change(string message, Action<Rules> change)
    {
        change(_auto.Rules);
        _auto.SaveRules();
        Status.Info(message);
        Reload();
    }

    private IEnumerable<CommandContextItem> AutomationActions()
    {
        var rules = _auto.Rules;
        yield return Ui.Action(rules.Enabled ? "Disable Automatic Switching" : "Enable Automatic Switching", rules.Enabled ? "" : "", () =>
            Change(rules.Enabled ? "Automatic switching off" : "Automatic switching on – make sure the Raycast Power Plan automation is off", r =>
            {
                r.Enabled = !r.Enabled;
                r.PausedUntil = null;
            }), Ui.CtrlShift(VirtualKey.T));

        if (rules.Enabled && rules.Paused)
        {
            yield return Ui.Action("Resume Now", "", () => Change("Automatic switching resumed", r => r.PausedUntil = null));
        }

        if (rules.Enabled && !rules.Paused)
        {
            foreach (var (title, span) in PauseOptions)
            {
                yield return Ui.Action($"Pause for {title}", "", () => Change("Automatic switching paused", r => r.PausedUntil = span is { } s ? DateTime.Now + s : DateTime.Today.AddDays(1).AddHours(6)));
            }
        }

        yield return new CommandContextItem(new GameDetectionPage(_auto)) { Title = "Game Detection", RequestedShortcut = Ui.Ctrl(VirtualKey.G) };
        yield return Ui.Action("Refresh", "", Reload, Ui.Ctrl(VirtualKey.R));
    }
}

internal sealed partial class GameDetectionPage : AsyncListPage
{
    private readonly PowerAutomation _auto;

    public GameDetectionPage(PowerAutomation auto)
    {
        _auto = auto;
        Id = "game-detection";
        Name = "Game Detection";
        Title = "Game Detection";
        Icon = Ui.Glyph("");
        PlaceholderText = "Filter running programs …";
        MaxAge = TimeSpan.FromSeconds(3);
    }

    protected override Task<Batch> Load(int page, CancellationToken token) => Task.Run(() =>
    {
        var processes = PowerApi.Processes();
        var games = _auto.DetectGames(processes, PowerApi.GetForeground());
        var gameNames = games.Select(g => g.Process.Name.ToLowerInvariant()).ToHashSet();
        var items = new List<IListItem>();

        if (games.Count == 0)
        {
            items.Add(new ListItem(new AnonymousCommand(Reload) { Name = "Refresh", Result = CommandResult.KeepOpen() })
            {
                Title = "No game running",
                Subtitle = "Start a game, or mark a program below as a game",
                Icon = Ui.Glyph(""),
                Section = "Detected Games",
            });
        }

        items.AddRange(games.Select(g => Item(g.Process, g, "Detected Games")));

        foreach (var name in _auto.Rules.CustomGames)
        {
            items.Add(Rule(name, Ui.Blue("Game")));
        }

        foreach (var name in _auto.Rules.Excluded)
        {
            items.Add(Rule(name, Ui.Gray("Never a game")));
        }

        // Running programs outside Windows' own folder, one per exe name
        var apps = processes
            .Where(p => p.Path is not null && !p.Path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) && !gameNames.Contains(p.Name.ToLowerInvariant()))
            .GroupBy(p => p.Name.ToLowerInvariant())
            .Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
        items.AddRange(apps.Select(p => Item(p, null, "Other Running Programs")));
        return new Batch([.. items]);
    });

    private void Change(string message, Action<Rules> change)
    {
        change(_auto.Rules);
        _auto.SaveRules();
        Status.Info(message);
        Reload();
    }

    private void AddGame(string name)
    {
        var key = name.ToLowerInvariant();
        Change($"{name} counts as a game", r =>
        {
            if (!r.CustomGames.Contains(key))
            {
                r.CustomGames.Add(key);
            }

            r.Excluded.Remove(key);
        });
    }

    private void Exclude(string name)
    {
        var key = name.ToLowerInvariant();
        Change($"{name} is never treated as a game", r =>
        {
            if (!r.Excluded.Contains(key))
            {
                r.Excluded.Add(key);
            }

            r.CustomGames.Remove(key);
        });
    }

    private ListItem Rule(string name, Tag tag) => new(new AnonymousCommand(() => Change($"Rule for {name} removed", r =>
    {
        r.CustomGames.Remove(name);
        r.Excluded.Remove(name);
    }))
    { Name = "Remove Rule", Result = CommandResult.KeepOpen() })
    {
        Title = name,
        Icon = Ui.Glyph(""),
        Section = "My Rules",
        Tags = [tag],
    };

    private ListItem Item(Proc p, Detection? detection, string section)
    {
        var library = _auto.LibraryOf(p.Path);
        var excluded = _auto.IsExcluded(p.Name);
        var commands = new List<IContextItem>();
        var exclude = Ui.Action("Never Treat as Game", "", () => Exclude(p.Name));
        var add = Ui.Action("Treat as Game", "", () => AddGame(p.Name));
        if (detection is not null || library is not null)
        {
            commands.Add(exclude);
        }

        if (detection is null || excluded)
        {
            commands.Add(add);
        }

        if (p.Path is not null)
        {
            commands.Add(new CommandContextItem(new ShowFileInFolderCommand(p.Path)) { Title = "Show in Explorer" });
        }

        commands.Add(Ui.Copy(p.Path ?? p.Name, "Copy Path"));
        commands.Add(Ui.Action("Refresh", "", Reload, Ui.Ctrl(VirtualKey.R)));

        var tags = new List<ITag>();
        if (detection is not null)
        {
            tags.Add(detection.Reason switch { "custom" => Ui.Blue("My list"), "fullscreen" => Ui.Orange("Fullscreen"), _ => Ui.Purple("Game library") });
        }
        else if (library is not null)
        {
            tags.Add(Ui.Gray("Excluded"));
        }

        return new ListItem(commands[0] is CommandContextItem first ? first.Command! : new NoOpCommand())
        {
            Title = System.IO.Path.GetFileNameWithoutExtension(p.Name),
            Subtitle = p.Path ?? string.Empty,
            Icon = p.Path is not null ? new IconInfo(p.Path) : Ui.Glyph(""),
            Section = section,
            Tags = [.. tags],
            MoreCommands = [.. commands.Skip(1)],
        };
    }
}
