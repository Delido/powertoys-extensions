using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.Amp;

[Guid("A8FEC8D3-23D2-4CF0-8D95-2E549EF2F024")]
public sealed partial class AmpExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new AmpProvider();
}

public sealed partial class AmpProvider : CommandProvider
{
    public const string ProviderId = "delido.amp";
    private readonly ICommandItem[] _commands;

    public AmpProvider()
    {
        var api = new AmpApi(new AmpSettings());
        Id = ProviderId;
        DisplayName = "AMP";
        Icon = Ui.Glyph("");
        Settings = api.Settings.Settings;
        _commands =
        [
            new CommandItem(new ServersPage(api)) { Title = "Manage Servers", Subtitle = "AMP" },
            new CommandItem(new AppsPage(api, null)) { Title = "Create Game Server", Subtitle = "AMP" },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem? GetCommandItem(string id) => _commands.FirstOrDefault(c => c.Command?.Id == id);
}

internal static class State
{
    public static (Tag Tag, string Glyph) Of(Instance i)
    {
        if (i.Suspended)
        {
            return (Ui.Red("Suspended"), "");
        }

        if (!i.Running)
        {
            return (Ui.Gray("Instance Offline"), "");
        }

        var (label, color) = i.AppState switch
        {
            0 => ("Stopped", 'g'),
            5 => ("Pre-Start", 'y'),
            7 => ("Configuring", 'y'),
            10 => ("Starting", 'y'),
            20 => ("Running", 'G'),
            30 => ("Restarting", 'y'),
            40 => ("Stopping", 'y'),
            45 => ("Preparing Sleep", 'y'),
            50 => ("Sleeping", 'b'),
            60 => ("Waiting", 'y'),
            70 => ("Installing", 'b'),
            75 => ("Updating", 'b'),
            80 => ("Awaiting Input", 'o'),
            100 => ("Failed", 'r'),
            200 => ("Suspended", 'r'),
            250 => ("Maintenance", 'o'),
            -1 => ("Undefined", 'g'),
            _ => ($"State {i.AppState}", 'g'),
        };
        var tag = color switch { 'G' => Ui.Green(label), 'y' => Ui.Yellow(label), 'b' => Ui.Blue(label), 'o' => Ui.Orange(label), 'r' => Ui.Red(label), _ => Ui.Gray(label) };
        var glyph = i.AppState switch { AmpApi.Ready => "", AmpApi.Failed => "", AmpApi.Stopped => "", _ => "" };
        return (tag, glyph);
    }

    public static string Bytes(double? bytes)
    {
        if (bytes is not { } v)
        {
            return string.Empty;
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (v >= 1024 && unit < units.Length - 1)
        {
            v /= 1024;
            unit++;
        }

        return $"{v.ToString(v >= 10 || unit == 0 ? "0" : "0.0")} {units[unit]}";
    }
}

internal sealed partial class ServersPage : AsyncListPage
{
    private readonly AmpApi _api;

    public ServersPage(AmpApi api)
    {
        _api = api;
        Id = "amp.manage-servers";
        Name = "Open";
        Title = "AMP Servers";
        Icon = Ui.Glyph("");
        PlaceholderText = "Filter instances …";
        MaxAge = TimeSpan.FromSeconds(5);
    }

    protected override async Task<Batch> Load(int page, CancellationToken token) =>
        new([.. (await _api.Instances(token)).Select(Item)]);

    protected override ICommandItem? Empty() =>
        !_api.Settings.Configured
            ? Ui.SetupItem(AmpProvider.ProviderId, "AMP")
            : new CommandItem(new AppsPage(_api, Reload)) { Title = "No instances found", Subtitle = "Enter creates a game server", Icon = Ui.Glyph("") };

    private void RefreshSoon() => _ = Task.Delay(1500).ContinueWith(_ => Reload());

    private void Run(string working, string done, Func<Task> task) => Status.Start(working, done, task, RefreshSoon);

    private ListItem Item(Instance i)
    {
        var (tag, glyph) = State.Of(i);
        var tags = new List<ITag>();
        if (i.Running)
        {
            if (i.Metrics.TryGetValue("Active Users", out var users))
            {
                tags.Add(Ui.Tag($"👥 {users.RawValue}/{users.MaxValue}"));
            }

            if (i.Metrics.TryGetValue("CPU Usage", out var cpu))
            {
                tags.Add(Ui.Tag($"CPU {cpu.Percent}%"));
            }

            if (i.Metrics.TryGetValue("Memory Usage", out var mem))
            {
                tags.Add(Ui.Tag(mem.RawValue >= 1024 ? $"RAM {mem.RawValue / 1024:0.0} GB" : $"RAM {mem.RawValue} MB"));
            }
        }

        tags.Add(tag);
        var name = i.FriendlyName;
        var id = i.InstanceID;
        var commands = new List<IContextItem>();
        ICommand primary;

        if (i.Running)
        {
            var console = new CommandContextItem(new ConsolePage(_api, i)) { Title = "Open Console", RequestedShortcut = Ui.Ctrl(VirtualKey.T) };
            var start = Ui.Action("Start Server", "", () => Run($"Starting {name} …", $"{name} started", () => _api.Action(id, "Core/Start")));
            primary = i.AppStopped ? start.Command! : console.Command!;
            commands.Add(i.AppStopped ? console : start);
            if (i.AppReady)
            {
                commands.Add(Ui.Confirm("Restart Server", "", "Restart server?", name, () => Run($"Restarting {name} …", $"{name} restarted", () => _api.Action(id, "Core/Restart")), shortcut: Ui.CtrlShift(VirtualKey.R)));
                commands.Add(Ui.Confirm("Stop Server", "", "Stop server?", name, () => Run($"Stopping {name} …", $"{name} stopped", () => _api.Action(id, "Core/Stop")), critical: true, shortcut: Ui.Ctrl(VirtualKey.D)));
            }

            commands.Add(Ui.Confirm("Update Game Server", "", "Update game server?", "It will be stopped if it is running.", () => Run($"Updating {name} …", "Update started", () => _api.Action(id, "Core/UpdateApplication"))));
            commands.Add(Ui.Confirm("Kill Server", "", "Kill server process?", "Unsaved progress may be lost.", () => Run($"Killing {name} …", $"{name} killed", () => _api.Action(id, "Core/Kill")), critical: true));
            commands.Add(new CommandContextItem(new InstanceDetailsPage(_api, i)) { Title = "Show Server Details", RequestedShortcut = Ui.Ctrl(VirtualKey.I) });
            commands.Add(new CommandContextItem(new PlayersPage(_api, i)) { Title = "Players", RequestedShortcut = Ui.Ctrl(VirtualKey.P) });
            commands.Add(new CommandContextItem(new BackupsPage(_api, i)) { Title = "Backups", RequestedShortcut = Ui.Ctrl(VirtualKey.B) });
            commands.Add(new CommandContextItem(new InstanceSettingsPage(_api, i)) { Title = "Settings", RequestedShortcut = Ui.Ctrl(VirtualKey.G) });
            commands.Add(new CommandContextItem(new SchedulePage(_api, i)) { Title = "Schedule", RequestedShortcut = Ui.Ctrl(VirtualKey.J) });
        }
        else
        {
            primary = new AnonymousCommand(() => Run($"Starting {name} …", $"{name} started", () => _api.StartInstanceAndServer(i))) { Name = "Start Server", Icon = Ui.Glyph(""), Result = CommandResult.KeepOpen() };
            commands.Add(Ui.Action("Start Instance Only", "", () => Run($"Starting instance {name} …", "Instance started", () => _api.Action(AmpApi.Ads, "ADSModule/StartInstance", new() { ["InstanceName"] = i.InstanceName }))));
        }

        var edit = new FormBuilder()
            .Input("friendlyName", "Name", i.FriendlyName, required: true)
            .Input("description", "Description", i.Description ?? string.Empty, multiline: true)
            .Toggle("startOnBoot", "Start this instance when the AMP host boots", i.StartOnBoot)
            .Text($"Internal name: {i.InstanceName} (cannot be changed)", subtle: true)
            .Build("Save");
        commands.Add(new CommandContextItem(new FormPage($"Edit {name}", edit, inputs =>
        {
            Run("Saving instance …", "Instance saved", () => _api.UpdateInfo(i, inputs.Str("friendlyName"), inputs.Str("description"), inputs.Bool("startOnBoot")));
            return CommandResult.GoBack();
        })) { Title = "Edit Instance", RequestedShortcut = Ui.Ctrl(VirtualKey.E) });
        commands.Add(Ui.Confirm("Update AMP on This Instance", "", "Update AMP on this instance?", "It will be restarted if running.", () => Run("Updating AMP …", "Update started", () => _api.Upgrade(i))));
        if (i.Running)
        {
            commands.Add(Ui.Confirm("Stop Instance", "", "Stop the whole AMP instance?", name, () => Run($"Stopping instance {name} …", "Instance stopped", () => _api.Action(AmpApi.Ads, "ADSModule/StopInstance", new() { ["InstanceName"] = i.InstanceName })), critical: true));
        }

        commands.Add(i.Suspended
            ? Ui.Action("Unsuspend Instance", "", () => Run("Unsuspending …", "Instance unsuspended", () => _api.SetSuspended(i, false)))
            : Ui.Confirm("Suspend Instance", "", "Suspend instance?", "It will be stopped and cannot be started until unsuspended.", () => Run("Suspending …", "Instance suspended", () => _api.SetSuspended(i, true))));
        commands.Add(Ui.Confirm("Delete Instance", "", $"Delete \"{name}\" permanently?", "The instance and ALL its files, worlds and backups will be deleted. This cannot be undone.", () => Run($"Deleting {name} …", "Deletion started", () => _api.Delete(i)), critical: true));
        commands.Add(Ui.OpenUrl(_api.PanelUrl(id), "Open in AMP Panel", Ui.Ctrl(VirtualKey.O)));
        if (i.Endpoints.FirstOrDefault() is { } endpoint)
        {
            commands.Add(Ui.Copy(endpoint.Address, "Copy Connection Address", Ui.CtrlShift(VirtualKey.C)));
        }

        commands.Add(Ui.Action("Refresh", "", Reload, Ui.Ctrl(VirtualKey.R)));
        commands.Add(new CommandContextItem(new AppsPage(_api, RefreshSoon)) { Title = "Create Game Server", RequestedShortcut = Ui.Ctrl(VirtualKey.N) });
        commands.Add(Ui.Confirm("Start All Instances", "", "Start all AMP instances?", string.Empty, () => Run("Starting all …", "All instances started", _api.StartAll)));
        commands.Add(Ui.Confirm("Stop All Instances", "", "Stop all AMP instances?", "All game servers will go offline.", () => Run("Stopping all …", "All instances stopped", _api.StopAll), critical: true));
        commands.Add(Ui.Confirm("Update AMP on All Instances", "", "Update AMP on all instances?", "Running instances will be restarted.", () => Run("Updating all …", "Update started", _api.UpgradeAll)));

        return new ListItem(primary)
        {
            Title = name,
            Subtitle = i.ModuleDisplayName ?? i.Module,
            Icon = Ui.Glyph(glyph),
            Tags = [.. tags],
            MoreCommands = [.. commands],
        };
    }
}

/// <summary>Live console: polls every 2 s while open; the search box is the command line.</summary>
internal sealed partial class ConsolePage : DynamicListPage
{
    private const int MaxLines = 500;
    private readonly AmpApi _api;
    private readonly Instance _instance;
    private readonly List<ConsoleEntry> _entries = [];
    private Timer? _timer;
    private DateTime _lastSeen;

    public ConsolePage(AmpApi api, Instance instance)
    {
        _api = api;
        _instance = instance;
        Name = "Open Console";
        Title = $"Console · {instance.FriendlyName}";
        Icon = Ui.Glyph("");
        PlaceholderText = "Type a console command and press Enter …";
        EmptyContent = Ui.EmptyItem("Connecting …", string.Empty, "");
    }

    public override IListItem[] GetItems()
    {
        _lastSeen = DateTime.UtcNow;
        _timer ??= new Timer(_ => _ = Poll(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));

        var items = new List<IListItem>();
        var command = SearchText.Trim();
        if (command.Length > 0)
        {
            items.Add(new ListItem(new AnonymousCommand(() =>
            {
                Status.Start("Sending …", $"Sent: {command}", () => _api.SendConsole(_instance.InstanceID, command), () =>
                {
                    SearchText = string.Empty;
                    _ = Poll();
                });
            })
            { Name = "Send", Result = CommandResult.KeepOpen() })
            {
                Title = $"Send \"{command}\"",
                Icon = Ui.Glyph(""),
            });
        }

        lock (_entries)
        {
            var all = string.Join("\n", _entries.Select(e => $"[{e.Source}] {e.Message}"));
            items.AddRange(Enumerable.Reverse(_entries).Select(e => new ListItem(new CopyTextCommand(e.Message) { Name = "Copy Line" })
            {
                Title = e.Message,
                Subtitle = e.Source,
                Icon = Ui.Glyph(""),
                Tags = e.Type.Contains("error", StringComparison.OrdinalIgnoreCase) ? [Ui.Red("Error")]
                    : e.Type.Contains("warn", StringComparison.OrdinalIgnoreCase) ? [Ui.Orange("Warning")]
                    : e.Type.Contains("chat", StringComparison.OrdinalIgnoreCase) ? [Ui.Blue("Chat")]
                    : e.Time is { } t ? [Ui.Tag(t.ToLocalTime().ToString("T"))] : [],
                MoreCommands =
                [
                    Ui.Copy(all, "Copy Entire Console", Ui.CtrlShift(VirtualKey.C)),
                    Ui.Action("Clear View", "", () =>
                    {
                        lock (_entries)
                        {
                            _entries.Clear();
                        }

                        RaiseItemsChanged();
                    }, Ui.Ctrl(VirtualKey.L)),
                ],
            }));
        }

        return [.. items];
    }

    public override void UpdateSearchText(string oldSearch, string newSearch) => RaiseItemsChanged();

    private async Task Poll()
    {
        // stop polling once the page has not been looked at for a while
        if (DateTime.UtcNow - _lastSeen > TimeSpan.FromMinutes(10))
        {
            _timer?.Dispose();
            _timer = null;
            return;
        }

        try
        {
            var updates = await _api.Console(_instance.InstanceID, CancellationToken.None);
            if (updates.Length == 0)
            {
                EmptyContent = Ui.EmptyItem("No console output yet", string.Empty, "");
                return;
            }

            lock (_entries)
            {
                _entries.AddRange(updates);
                if (_entries.Count > MaxLines)
                {
                    _entries.RemoveRange(0, _entries.Count - MaxLines);
                }
            }

            RaiseItemsChanged();
        }
        catch (Exception ex)
        {
            EmptyContent = Ui.EmptyItem("Could not read console", ex.Message, "");
        }
    }
}

internal sealed partial class InstanceDetailsPage : AsyncMarkdownPage
{
    private readonly AmpApi _api;
    private readonly Instance _i;
    private bool _revealed;

    public InstanceDetailsPage(AmpApi api, Instance i)
    {
        _api = api;
        _i = i;
        Name = "Show Server Details";
        Title = i.FriendlyName;
        Icon = Ui.Glyph("");
    }

    protected override async Task<(string Markdown, IDetails? Details, IContextItem[] Commands)> Load(CancellationToken token)
    {
        var (passwords, settings) = await _api.ServerInfo(_i.InstanceID, token);
        var lines = new List<string> { $"# {Core.Markdown.Escape(_i.FriendlyName)}", string.Empty, "## Passwords", string.Empty };
        if (passwords.Length == 0)
        {
            lines.Add("_No game server password set._");
        }

        lines.AddRange(passwords.Select(p => $"- **{p.Label}:** `{(_revealed ? p.Value : new string('•', Math.Clamp(p.Value.Length, 6, 16)))}`"));
        if (settings.Length > 0)
        {
            lines.AddRange(["", "## Settings", ""]);
            lines.AddRange(settings.Select(s => $"- **{s.Label}:** {s.Value}"));
        }

        var (tag, _) = State.Of(_i);
        var meta = new List<IDetailsElement> { Ui.Tags("Status", tag), Ui.Label("Game", _i.ModuleDisplayName ?? _i.Module) };
        meta.AddRange(_i.Endpoints.Select(e => Ui.Label(e.DisplayName, e.Address)));
        meta.Add(Ui.Label("Instance", _i.InstanceName));

        var commands = new List<IContextItem>();
        if (passwords.Length > 0)
        {
            commands.Add(Ui.Action(_revealed ? "Hide Passwords" : "Show Passwords", "", () =>
            {
                _revealed = !_revealed;
                Refresh();
            }, Ui.Ctrl(VirtualKey.Y)));
        }

        commands.AddRange(passwords.Select((p, n) => Ui.Copy(p.Value, $"Copy {p.Label}", n == 0 ? Ui.CtrlShift(VirtualKey.C) : null)));
        commands.AddRange(_i.Endpoints.Select(e => Ui.Copy(e.Address, $"Copy {e.DisplayName}")));
        commands.Add(Ui.OpenUrl(_api.PanelUrl(_i.InstanceID), "Open in AMP Panel", Ui.Ctrl(VirtualKey.O)));
        return (string.Join("\n", lines), new Details { Title = _i.FriendlyName, Metadata = [.. meta] }, [.. commands]);
    }
}

internal sealed partial class PlayersPage : AsyncListPage
{
    // Console commands for games whose moderation works via the server console
    private static readonly Dictionary<string, (string Label, Func<string, string> Command, bool Destructive)[]> Moderation = new()
    {
        ["Minecraft"] =
        [
            ("Kick", n => $"kick {n}", true),
            ("Ban", n => $"ban {n}", true),
            ("Make Operator", n => $"op {n}", false),
            ("Remove Operator", n => $"deop {n}", false),
            ("Whitelist", n => $"whitelist add {n}", false),
        ],
    };

    private readonly AmpApi _api;
    private readonly Instance _i;

    public PlayersPage(AmpApi api, Instance i)
    {
        _api = api;
        _i = i;
        Name = "Players";
        Title = $"Players · {i.FriendlyName}";
        Icon = Ui.Glyph("");
        PlaceholderText = "Filter players …";
        MaxAge = TimeSpan.FromSeconds(10);
        EmptyContent = Ui.EmptyItem("Nobody is online", string.Empty, "");
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        var moderation = Moderation.GetValueOrDefault(_i.Module) ?? [];
        return new([.. (await _api.Players(_i.InstanceID, token)).Select(p =>
        {
            var commands = new List<IContextItem>();
            foreach (var (label, command, destructive) in moderation)
            {
                void Send() => Status.Start($"{label} {p.Name} …", $"{label}: sent", () => _api.SendConsole(_i.InstanceID, command(p.Name)), Reload);
                commands.Add(destructive ? Ui.Confirm(label, "", $"{label} {p.Name}?", string.Empty, Send, critical: true) : Ui.Action(label, "", Send));
            }

            commands.Add(Ui.Copy(p.Name, "Copy Name"));
            if (p.Id.Length > 0)
            {
                commands.Add(Ui.Copy(p.Id, "Copy ID"));
            }

            commands.Add(Ui.Action("Refresh", "", Reload, Ui.Ctrl(VirtualKey.R)));
            return new ListItem(new CopyTextCommand(p.Name) { Name = "Copy Name" })
            {
                Title = p.Name,
                Subtitle = p.Id != p.Name ? p.Id : string.Empty,
                Icon = Ui.Glyph(""),
                MoreCommands = [.. commands],
            };
        })]);
    }
}

internal sealed partial class BackupsPage : AsyncListPage
{
    private readonly AmpApi _api;
    private readonly Instance _i;

    public BackupsPage(AmpApi api, Instance i)
    {
        _api = api;
        _i = i;
        Name = "Backups";
        Title = $"Backups · {i.FriendlyName}";
        Icon = Ui.Glyph("");
        PlaceholderText = "Filter backups …";
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        var id = _i.InstanceID;
        return new([.. (await _api.Backups(id, token)).Select(b =>
        {
            var tags = new List<ITag>();
            if (b.Automatic)
            {
                tags.Add(Ui.Tag("Auto"));
            }

            if (b.S3)
            {
                tags.Add(Ui.Blue("S3"));
            }

            if (State.Bytes(b.Size) is { Length: > 0 } size)
            {
                tags.Add(Ui.Tag(size));
            }

            if (b.Date is { } d)
            {
                tags.Add(Ui.Tag(Ui.Ago(d)));
            }

            var restore = Ui.Confirm("Restore Backup", "", "Restore this backup?", $"{_i.FriendlyName} will be stopped and its files replaced with \"{b.Name}\".",
                () => Status.Start("Restoring …", "Restore started", () => _api.RestoreBackup(id, b.Id), RefreshSoon), critical: true);
            return new ListItem(restore.Command!)
            {
                Title = b.Name,
                Subtitle = b.Description,
                Icon = Ui.Glyph(b.Sticky ? "" : ""),
                Tags = [.. tags],
                MoreCommands =
                [
                    TakeBackup(),
                    Ui.Action(b.Sticky ? "Unpin Backup" : "Pin Backup (Keep Forever)", "", () => Status.Start("Saving …", b.Sticky ? "Backup unpinned" : "Backup pinned", () => _api.SetBackupSticky(id, b.Id, !b.Sticky), Reload), Ui.CtrlShift(VirtualKey.P)),
                    Ui.Confirm("Delete Backup", "", "Delete this backup?", b.Name, () => Status.Start("Deleting backup …", "Backup deleted", () => _api.DeleteBackup(id, b.Id), Reload), critical: true, shortcut: Ui.Ctrl(VirtualKey.Delete)),
                    Ui.Action("Refresh", "", Reload, Ui.Ctrl(VirtualKey.R)),
                ],
            };
        })]);
    }

    protected override ICommandItem? Empty() => new CommandItem(TakeBackup().Command!) { Title = "No backups yet", Subtitle = "Enter takes a backup now", Icon = Ui.Glyph("") };

    private void RefreshSoon() => _ = Task.Delay(2000).ContinueWith(_ => Reload());

    private CommandContextItem TakeBackup()
    {
        var form = new FormBuilder()
            .Input("title", "Title", $"Backup {DateTime.Now:g}")
            .Input("description", "Description", placeholder: "Optional", multiline: true)
            .Toggle("sticky", "Keep forever (never delete automatically)")
            .Text("Depending on the game, AMP may stop the server while the backup is taken.", subtle: true)
            .Build("Take Backup");
        return new CommandContextItem(new FormPage($"New Backup · {_i.FriendlyName}", form, inputs =>
        {
            var title = inputs.Str("title") is { Length: > 0 } t ? t : "PowerToys Backup";
            Status.Start("Taking backup …", "Backup started", () => _api.TakeBackup(_i.InstanceID, title, inputs.Str("description"), inputs.Bool("sticky")), RefreshSoon);
            return CommandResult.GoBack();
        }, Ui.Glyph("")))
        {
            Title = "Take Backup",
            RequestedShortcut = Ui.Ctrl(VirtualKey.N),
        };
    }
}

internal sealed partial class InstanceSettingsPage : AsyncListPage
{
    private readonly AmpApi _api;
    private readonly Instance _i;
    private bool _revealed;

    public InstanceSettingsPage(AmpApi api, Instance i)
    {
        _api = api;
        _i = i;
        Name = "Settings";
        Title = $"Settings · {i.FriendlyName}";
        Icon = Ui.Glyph("");
        PlaceholderText = "Search settings …";
        ShowDetails = true;
    }

    protected override async Task<Batch> Load(int page, CancellationToken token) =>
        new([.. (await _api.SettingsOf(_i.InstanceID, token)).Select(Item)]);

    private string Display(AmpSetting s)
    {
        if (s.InputType == "password")
        {
            return s.Value.Length == 0 ? string.Empty : _revealed ? s.Value : "••••••••";
        }

        if (s.InputType == "checkbox")
        {
            return s.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "On" : "Off";
        }

        var option = s.Options.FirstOrDefault(o => o.Value == s.Value);
        return $"{(option.Title ?? s.Value)}{(s.Suffix.Length > 0 && s.Value.Length > 0 ? $" {s.Suffix}" : string.Empty)}";
    }

    private ListItem Item(AmpSetting s)
    {
        var commands = new List<IContextItem>
        {
            Ui.Action(_revealed ? "Hide Passwords" : "Show Passwords", "", () =>
            {
                _revealed = !_revealed;
                Reload();
            }, Ui.Ctrl(VirtualKey.Y)),
            Ui.Copy(s.Value, "Copy Value", Ui.CtrlShift(VirtualKey.C)),
            Ui.Action("Refresh", "", Reload, Ui.Ctrl(VirtualKey.R)),
        };
        ICommand primary = s.ReadOnly ? new CopyTextCommand(s.Value) { Name = "Copy Value" } : EditForm(s);
        return new ListItem(primary)
        {
            Title = s.Name,
            Subtitle = Display(s),
            Icon = Ui.Glyph(s.InputType == "password" ? "" : s.ReadOnly ? "" : ""),
            Section = s.Subcategory.Length > 0 ? $"{s.Category} › {s.Subcategory}" : s.Category,
            Details = new Details { Title = s.Name, Body = s.Description, Metadata = [Ui.Label("Value", Display(s)), Ui.Label("Node", s.Node)] },
            MoreCommands = [.. commands],
        };
    }

    private FormPage EditForm(AmpSetting s)
    {
        var form = new FormBuilder();
        if (s.InputType == "checkbox")
        {
            form.Toggle("value", s.Name, s.Value.Equals("true", StringComparison.OrdinalIgnoreCase));
        }
        else if (s.Options.Length > 0)
        {
            form.Choice("value", s.Name, s.Options.Select(o => (o.Title, o.Value)), s.Value);
        }
        else
        {
            form.Input("value", s.Suffix.Length > 0 ? $"{s.Name} ({s.Suffix})" : s.Name, s.Value, s.Placeholder, multiline: s.InputType == "textarea" || s.Value.Length > 80, password: s.InputType == "password");
        }

        if (s.Description.Length > 0)
        {
            form.Text(s.Description, subtle: true);
        }

        form.Text("Most settings take effect after the server is restarted.", subtle: true);
        return new FormPage(s.Name, form.Build("Save"), inputs =>
        {
            var value = s.InputType == "checkbox" ? (inputs.Bool("value") ? "True" : "False") : inputs.Str("value");
            Status.Start("Saving setting …", "Setting saved", () => _api.SetSetting(_i.InstanceID, s.Node, value), Reload);
            return CommandResult.GoBack();
        });
    }
}

internal sealed partial class SchedulePage : AsyncListPage
{
    private readonly AmpApi _api;
    private readonly Instance _i;

    public SchedulePage(AmpApi api, Instance i)
    {
        _api = api;
        _i = i;
        Name = "Schedule";
        Title = $"Schedule · {i.FriendlyName}";
        Icon = Ui.Glyph("");
        PlaceholderText = "Filter scheduled tasks …";
        EmptyContent = new CommandItem(new OpenUrlCommand(api.PanelUrl(i.InstanceID)) { Name = "Open in AMP Panel" }) { Title = "No scheduled tasks", Subtitle = "Create schedules in the AMP panel", Icon = Ui.Glyph("") };
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        var id = _i.InstanceID;
        return new([.. (await _api.Schedule(id, token)).Select(t =>
        {
            var run = Ui.Confirm("Run Now", "", "Run these tasks now?", t.Tasks.Length > 0 ? string.Join(", ", t.Tasks) : t.Description,
                () => Status.Start("Running …", "Tasks started", () => _api.RunTrigger(id, t.Id)));
            return new ListItem(run.Command!)
            {
                Title = t.Description,
                Subtitle = string.Join(" → ", t.Tasks),
                Icon = Ui.Glyph(""),
                Tags = [Ui.Tag($"{t.Tasks.Length} task{(t.Tasks.Length == 1 ? string.Empty : "s")}"), t.Enabled ? Ui.Green("Enabled") : Ui.Gray("Disabled")],
                MoreCommands =
                [
                    Ui.Action(t.Enabled ? "Disable" : "Enable", t.Enabled ? "" : "", () => Status.Start("Saving …", t.Enabled ? "Disabled" : "Enabled", () => _api.SetTriggerEnabled(id, t.Id, !t.Enabled), Reload)),
                    Ui.OpenUrl(_api.PanelUrl(id), "Edit in AMP Panel", Ui.Ctrl(VirtualKey.O)),
                    Ui.Action("Refresh", "", Reload, Ui.Ctrl(VirtualKey.R)),
                ],
            };
        })]);
    }
}

/// <summary>The games AMP can deploy; Enter opens the creation form.</summary>
internal sealed partial class AppsPage : AsyncListPage
{
    private readonly AmpApi _api;
    private readonly Action? _created;

    public AppsPage(AmpApi api, Action? created)
    {
        _api = api;
        _created = created;
        Id = created is null ? "create-server" : string.Empty;
        Name = "Create Game Server";
        Title = "Create Game Server";
        Icon = Ui.Glyph("");
        PlaceholderText = "Search games …";
        ShowDetails = true;
    }

    protected override async Task<Batch> Load(int page, CancellationToken token) =>
        new([.. (await _api.Apps(token)).Select(app =>
        {
            var form = new FormBuilder()
                .Text(app.Name, bold: true)
                .Input("friendlyName", "Server Name", $"{app.Name} Server", required: true)
                .Toggle("install", "Start the instance and install the game right away", true)
                .Toggle("startOnBoot", "Start when the AMP host boots", true)
                .Text("Ports, memory and other options use AMP's defaults and can be changed later in Settings.", subtle: true)
                .Build("Create Server");
            var tags = new List<ITag>();
            if (app.Deprecated.Length > 0)
            {
                tags.Add(Ui.Orange("Deprecated"));
            }

            if (app.Author.Length > 0)
            {
                tags.Add(Ui.Tag(app.Author));
            }

            return new ListItem(new FormPage($"New {app.Name} Server", form, inputs =>
            {
                var name = inputs.Str("friendlyName");
                var install = inputs.Bool("install");
                Status.Start(install ? $"Creating and installing {name} … (can take minutes)" : $"Creating {name} …", install ? $"{name}: installation started – follow it in the console" : $"{name} created", async () =>
                {
                    await _api.CreateServer(app, name, inputs.Bool("startOnBoot"));
                    _created?.Invoke();
                    if (install)
                    {
                        await _api.InstallNewServer(name);
                    }
                }, _created);
                return CommandResult.GoHome();
            }))
            {
                Title = app.Name,
                Subtitle = app.Description,
                Icon = Ui.Glyph(""),
                Tags = [.. tags],
                Details = new Details
                {
                    Title = app.Name,
                    HeroImage = app.Image is null ? new IconInfo(string.Empty) : new IconInfo(app.Image),
                    Body = string.IsNullOrEmpty(app.Deprecated) ? app.Description : $"{app.Description}\n\n**Deprecated:** {app.Deprecated}",
                    Metadata = [Ui.Label("Module", app.Module), Ui.Label("Author", app.Author), Ui.Label("Tags", string.Join(", ", app.Tags))],
                },
            };
        })]);

    protected override ICommandItem? Empty() =>
        _api.Settings.Configured ? Ui.EmptyItem("No games found") : Ui.SetupItem(AmpProvider.ProviderId, "AMP");
}
