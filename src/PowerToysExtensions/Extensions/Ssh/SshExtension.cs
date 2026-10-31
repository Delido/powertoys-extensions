using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.Ssh;

[Guid("ADC097CC-3638-4EF1-AC55-6182E2CD8836")]
public sealed partial class SshExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new SshProvider();
}

public sealed class SshSettings : SettingsBase
{
    private readonly ChoiceSetSetting _terminal = new("terminal", "Terminal", "Terminal used to open SSH sessions", Terminal.Choices);
    private readonly TextSetting _profile = new("wtProfile", "Windows Terminal profile", "Optional profile name for new tabs, e.g. \"PowerShell\"", string.Empty);
    private readonly ChoiceSetSetting _browser = new("browser", "Tunnel browser", "Browser used for “Open in Browser via Tunnel” (separate profile, SOCKS proxy)", Choices(("Microsoft Edge", "edge"), ("Google Chrome", "chrome")));
    private readonly ChoiceSetSetting _sync = new("syncMinutes", "Auto sync", "How often team sources are refreshed", Choices(("Every hour", "60"), ("Every 15 minutes", "15"), ("Every 4 hours", "240"), ("Once a day", "1440"), ("Off", "0")));
    private readonly ToggleSetting _risky = new("blockRisky", "Block risky options in downloaded files", "Comments out ProxyCommand, LocalCommand, Match exec, KnownHostsCommand etc. in team files (ProxyJump stays allowed)", true);

    public SshSettings()
        : base("ssh")
    {
        Settings.Add(_terminal);
        Settings.Add(_profile);
        Settings.Add(_browser);
        Settings.Add(_sync);
        Settings.Add(_risky);
        Load();
    }

    public string TerminalChoice => _terminal.Value ?? "auto";

    public string? WtProfile => _profile.Value;

    public string Browser => _browser.Value ?? "edge";

    public TimeSpan SyncInterval => TimeSpan.FromMinutes(int.TryParse(_sync.Value, out var m) ? m : 60);

    public bool BlockRisky => _risky.Value;
}

public sealed partial class SshProvider : CommandProvider
{
    public const string ProviderId = "delido.ssh";
    private readonly ICommandItem[] _commands;
    private readonly Timer _sync;

    public SshProvider()
    {
        var settings = new SshSettings();
        var sources = new SshSources(() => settings.BlockRisky);
        var ctx = new SshContext(settings, sources);
        Id = ProviderId;
        DisplayName = "SSH Sessions";
        Icon = Ui.Glyph("\uE756");
        Settings = settings.Settings;
        _commands =
        [
            new CommandItem(new HostsPage(ctx)) { Title = "Search SSH Hosts", Subtitle = "SSH" },
            new CommandItem(new SourcesPage(ctx)) { Title = "Manage SSH Sources", Subtitle = "SSH · team host lists, personal defaults" },
        ];

        // replaces Raycast's hourly "Sync SSH Sources" command
        _sync = new Timer(_ => _ = ctx.SyncIfDue(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem? GetCommandItem(string id) => _commands.FirstOrDefault(c => c.Command?.Id == id);

    public override void Dispose()
    {
        _sync.Dispose();
        base.Dispose();
    }
}

/// <summary>Shared state and actions of the SSH pages.</summary>
internal sealed class SshContext(SshSettings settings, SshSources sources)
{
    private readonly string _usageFile = Path.Combine(Paths.For("ssh"), "usage.json");

    public SshSettings Settings => settings;

    public SshSources Sources => sources;

    public async Task SyncIfDue()
    {
        if (sources.SyncDue(settings.SyncInterval))
        {
            await sources.SyncAll();
        }
    }

    public void Sync(bool force, Action? after) => Status.Start("Syncing sources …", "Sources synced", async () =>
    {
        var results = await sources.SyncAll(force);
        var errors = results.Where(r => r.Status == "error").ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"{errors.Count} source(s) unreachable – using cached hosts: {string.Join("; ", errors.Select(e => $"{e.Source.Name}: {e.State.Error}"))}");
        }

        var updated = results.Count(r => r.Status == "updated");
        Status.Info(updated > 0 ? $"{updated} source(s) updated" : "Everything up to date");
    }, after);

    public void Connect(string alias, string? user = null, bool newWindow = false)
    {
        try
        {
            string[] args = user is null ? ["ssh", Terminal.SafeArg(alias)] : ["ssh", "-l", Terminal.SafeArg(user), Terminal.SafeArg(alias)];
            Terminal.Open(user is null ? alias : $"{user}@{alias}", args, settings.TerminalChoice, settings.WtProfile, newWindow);
            RecordUsage(alias);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Status.Error($"Could not open {alias}: {ex.Message}");
        }
    }

    public void OpenInVsCode(string alias)
    {
        try
        {
            Proc.Start("cmd.exe", "/c", "code", "--remote", $"ssh-remote+{Terminal.SafeArg(alias)}", "/");
            RecordUsage(alias);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Status.Error(ex.Message);
        }
    }

    /// <summary>Starts the SOCKS tunnel if needed (the user logs in in the terminal), then opens the browser through it.</summary>
    public void BrowserViaTunnel(HostEntry host) => _ = Task.Run(async () =>
    {
        var port = host.SocksPort!.Value;
        if (!await IsPortOpen(port))
        {
            Connect(host.Alias);
            Status.Info($"Waiting for tunnel {host.Alias} … log in in the terminal tab if asked");
            var until = DateTime.UtcNow.AddMinutes(2);
            while (!await IsPortOpen(port))
            {
                if (DateTime.UtcNow > until)
                {
                    Status.Error($"Tunnel {host.Alias} did not come up – nothing listens on 127.0.0.1:{port}");
                    return;
                }

                await Task.Delay(1000);
            }
        }

        var exe = (settings.Browser == "chrome"
            ? new[] { @"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe") }
            : [@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"]).FirstOrDefault(File.Exists);
        if (exe is null)
        {
            Status.Error("Browser not found – check the “Tunnel browser” setting");
            return;
        }

        // separate profile that routes everything through the tunnel; DNS is resolved on the far side
        var profile = Paths.For(Path.Combine("ssh", $"browser-profile-{port}"));
        Proc.Start(exe, $"--user-data-dir={profile}", $"--proxy-server=socks5://127.0.0.1:{port}", "--host-resolver-rules=MAP * ~NOTFOUND , EXCLUDE 127.0.0.1", "--no-first-run", "--no-default-browser-check", host.Web!);
    });

    public Dictionary<string, (int Count, DateTime Last)> Usage()
    {
        try
        {
            return File.Exists(_usageFile)
                ? JsonSerializer.Deserialize<Dictionary<string, UsageEntry>>(File.ReadAllText(_usageFile))!.ToDictionary(p => p.Key, p => (p.Value.Count, p.Value.Last))
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Frecency: frequent and recent hosts first.</summary>
    public static double Frecency((int Count, DateTime Last) e) => e.Count / (1 + ((DateTime.Now - e.Last).TotalDays / 7));

    private static async Task<bool> IsPortOpen(int port)
    {
        using var client = new TcpClient();
        try
        {
            using var cts = new CancellationTokenSource(800);
            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private void RecordUsage(string alias)
    {
        var usage = Usage();
        var entry = usage.GetValueOrDefault(alias);
        usage[alias] = (entry.Count + 1, DateTime.Now);
        File.WriteAllText(_usageFile, JsonSerializer.Serialize(usage.ToDictionary(p => p.Key, p => new UsageEntry(p.Value.Count, p.Value.Last))));
    }

    private sealed record UsageEntry(int Count, DateTime Last);
}

internal sealed partial class HostsPage : AsyncListPage
{
    private const int RecentCount = 6;
    private readonly SshContext _ctx;

    public HostsPage(SshContext ctx)
    {
        _ctx = ctx;
        Id = "ssh.search-hosts";
        Name = "Open";
        Title = "SSH Hosts";
        Icon = Ui.Glyph("\uE756");
        PlaceholderText = "Search SSH hosts …";
        ShowDetails = true;
        MaxAge = TimeSpan.FromSeconds(10);
    }

    protected override Task<Batch> Load(int page, CancellationToken token) => Task.Run(() =>
    {
        var (hosts, overridden) = SshConfig.Load();
        var usage = _ctx.Usage();
        var sources = _ctx.Sources.Sources();
        var states = _ctx.Sources.States();
        var names = sources.ToDictionary(s => s.Id, s => s.Name);
        var items = new List<IListItem>();

        if (sources.Count > 0 && !SshConfig.IsIntegrated())
        {
            items.Add(new ListItem(Ui.Confirm("Enable Team Hosts", "\uE71B", "Add the Include block to ~/.ssh/config?", "Team hosts become available to ssh, VS Code, WinSCP etc. Your own host entries keep priority. A backup of your config is created first.", () =>
            {
                SshConfig.Integrate();
                Status.Info("Team hosts enabled");
                Reload();
            }).Command!)
            {
                Title = "Enable team hosts for ssh",
                Subtitle = "Adds one Include block to ~/.ssh/config (backup is created)",
                Icon = Ui.Glyph("\uE7BA"),
                Section = "Setup",
            });
        }

        var offline = sources.Where(s => s.Enabled && states.GetValueOrDefault(s.Id)?.Error is not null).ToList();
        if (offline.Count > 0)
        {
            items.Add(new ListItem(new AnonymousCommand(() => _ctx.Sync(false, Reload)) { Name = "Try Again", Result = CommandResult.KeepOpen() })
            {
                Title = $"{string.Join(", ", offline.Select(s => s.Name))} unreachable",
                Subtitle = "Using cached hosts from the last successful sync",
                Icon = Ui.Glyph("\uEB5E"),
                Section = "Sources",
            });
        }

        var recent = hosts.Where(h => usage.ContainsKey(h.Alias)).OrderByDescending(h => SshContext.Frecency(usage[h.Alias])).Take(RecentCount).ToList();
        items.AddRange(recent.Select(h => Item(h, "Recent", names, overridden)));
        var recentSet = recent.Select(h => h.Alias).ToHashSet();
        var groups = hosts.Where(h => !recentSet.Contains(h.Alias))
            .GroupBy(h => h.Group is not null ? GroupParts(h.Group).Section : h.Origin == "local" ? "Local" : names.GetValueOrDefault(h.Origin, h.Origin))
            .OrderBy(g => g.Key == "Local" ? string.Empty : g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var list = g.ToList();
            items.AddRange(list.Select(h => Item(h, $"{g.Key} ({list.Count})", names, overridden)));
        }

        return new Batch([.. items]);
    });

    protected override ICommandItem? Empty() => new CommandItem(AddHost().Command!)
    {
        Title = "No SSH hosts found",
        Subtitle = "Enter adds a host to ~/.ssh/config, or configure a team source",
        Icon = Ui.Glyph("\uE756"),
    };

    /// <summary>Splits "A\B\C\D" (RDM style) or "A / B / C" into a section (first two levels) and the rest.</summary>
    private static (string Section, string Tail, string[] All) GroupParts(string group)
    {
        var all = Regex.Split(group, @"\s*[\\/›]\s*").Where(s => s.Length > 0).ToArray();
        return (string.Join(" › ", all.Take(2)), string.Join(" › ", all.Skip(2)), all);
    }

    private CommandContextItem AddHost()
    {
        var form = new FormBuilder()
            .Input("alias", "Alias", placeholder: "web01", required: true)
            .Input("hostName", "Host / IP", placeholder: "10.0.0.11 or web01.example.com", required: true)
            .Input("user", "User", placeholder: "empty = your default user")
            .Input("port", "Port", placeholder: "22")
            .Input("proxyJump", "Jump Host", placeholder: "optional, e.g. jump01")
            .Input("identityFile", "Identity File", placeholder: "optional, e.g. ~/.ssh/id_ed25519")
            .Input("group", "Group", placeholder: "optional, e.g. Customers / ACME")
            .Input("tags", "Tags", placeholder: "optional, comma separated")
            .Input("description", "Description", placeholder: "optional")
            .Build("Add to ~/.ssh/config");
        return new CommandContextItem(new FormPage("Add SSH Host", form, v =>
        {
            var alias = v.Str("alias");
            if (!Regex.IsMatch(alias, @"^[\w.-]+$") || v.Str("hostName").Length == 0)
            {
                return CommandResult.ShowToast(new ToastArgs { Message = "Alias (letters, digits, . - _) and host are required", Result = CommandResult.KeepOpen() });
            }

            var lines = new List<string>();
            if (v.Str("group").Length > 0)
            {
                lines.Add($"# group: {v.Str("group")}");
            }

            if (v.Str("tags").Length > 0)
            {
                lines.Add($"# tags: {v.Str("tags")}");
            }

            if (v.Str("description").Length > 0)
            {
                lines.Add($"# description: {v.Str("description")}");
            }

            lines.Add($"Host {alias}");
            lines.Add($"    HostName {v.Str("hostName")}");
            if (v.Str("user").Length > 0)
            {
                lines.Add($"    User {v.Str("user")}");
            }

            if (v.Str("port") is { Length: > 0 } port && port != "22")
            {
                lines.Add($"    Port {port}");
            }

            if (v.Str("proxyJump").Length > 0)
            {
                lines.Add($"    ProxyJump {v.Str("proxyJump")}");
            }

            if (v.Str("identityFile").Length > 0)
            {
                lines.Add($"    IdentityFile \"{v.Str("identityFile")}\"");
            }

            SshConfig.AppendLocalHost(string.Join("\n", lines));
            Status.Info(SshConfig.Load().Hosts.Count(h => h.Alias == alias) > 0 ? $"{alias} added" : $"{alias} added (an existing entry takes priority)");
            Reload();
            return CommandResult.GoBack();
        }, Ui.Glyph("\uE710")))
        {
            Title = "Add Host",
            RequestedShortcut = Ui.Ctrl(VirtualKey.N),
        };
    }

    private ListItem Item(HostEntry h, string section, Dictionary<string, string> names, HashSet<string> overridden)
    {
        var target = $"{(h.User is null ? string.Empty : $"{h.User}@")}{h.HostName ?? h.Alias}{(h.Port is { } p && p != "22" ? $":{p}" : string.Empty)}";
        var origin = h.Origin == "local" ? "Local" : names.GetValueOrDefault(h.Origin, h.Origin);
        var tags = new List<ITag>();
        if (h.SocksPort is { } socks)
        {
            tags.Add(Ui.Purple($"SOCKS :{socks}"));
        }

        if (h.ProxyJump is not null)
        {
            tags.Add(Ui.Tag($"via {h.ProxyJump}"));
        }

        if (h.Group is not null && GroupParts(h.Group).Tail is { Length: > 0 } rest)
        {
            tags.Add(Ui.Tag($"📁 {rest}"));
        }

        tags.AddRange(h.Tags.Take(2).Select(t => (ITag)Ui.Tag(t)));
        if (h.Origin == "local" && overridden.Contains(h.Alias))
        {
            tags.Add(Ui.Orange("overrides team"));
        }

        tags.Add(Ui.Gray(origin));

        var commands = new List<IContextItem>();
        if (h.SocksPort is not null && h.Web is not null)
        {
            commands.Add(Ui.Action("Open in Browser via Tunnel", "\uE774", () => _ctx.BrowserViaTunnel(h), Ui.Ctrl(VirtualKey.B)));
        }

        commands.Add(Ui.Action("Connect in New Window", "\uE737", () => _ctx.Connect(h.Alias, newWindow: true), Ui.Ctrl(VirtualKey.O)));
        if (!h.TunnelOnly)
        {
            commands.Add(new CommandContextItem(new SftpPage(_ctx, h.Alias, null)) { Title = "Browse Files (SFTP)", RequestedShortcut = Ui.CtrlShift(VirtualKey.F) });
        }

        var asUser = new FormBuilder().Text($"{h.Alias} ({h.HostName ?? h.Alias})", subtle: true).Input("user", "User", h.User ?? string.Empty, "root", required: true).Build("Connect");
        commands.Add(new CommandContextItem(new FormPage($"Connect to {h.Alias}", asUser, v =>
        {
            _ctx.Connect(h.Alias, v.Str("user"));
            return CommandResult.Dismiss();
        }, Ui.Glyph("\uE77B")))
        { Title = "Connect as User …", RequestedShortcut = Ui.Ctrl(VirtualKey.U) });
        commands.Add(Ui.Action("Open in VS Code", "\uE943", () => _ctx.OpenInVsCode(h.Alias), Ui.CtrlShift(VirtualKey.V)));
        commands.Add(Ui.Copy($"ssh {h.Alias}", "Copy SSH Command", Ui.CtrlShift(VirtualKey.C)));
        commands.Add(Ui.Copy(h.HostName ?? h.Alias, "Copy Hostname", Ui.CtrlShift((VirtualKey)0xBE)));
        commands.Add(Ui.Copy(SshConfig.Block(h), "Copy Config Block"));
        commands.Add(AddHost());
        commands.Add(new CommandContextItem(new OpenFileCommand(SshConfig.ConfigPath)) { Title = "Open Local SSH Config" });
        commands.Add(Ui.Action("Sync Sources Now", "\uE72C", () => _ctx.Sync(false, Reload), Ui.Ctrl(VirtualKey.R)));
        commands.Add(new CommandContextItem(new SourcesPage(_ctx)) { Title = "Manage Sources" });

        var meta = new List<IDetailsElement>
        {
            Ui.Label("Host", h.HostName ?? h.Alias),
            Ui.Label("User", h.User ?? "(default)"),
        };
        if (h.Port is not null)
        {
            meta.Add(Ui.Label("Port", h.Port));
        }

        if (h.ProxyJump is not null)
        {
            meta.Add(Ui.Label("Jump Host", h.ProxyJump));
        }

        if (h.IdentityFile is not null)
        {
            meta.Add(Ui.Label("Identity File", h.IdentityFile));
        }

        meta.Add(Ui.Label("Source", origin));
        if (h.Group is not null)
        {
            meta.Add(Ui.Label("Group", h.Group));
        }

        if (h.Tags.Length > 0)
        {
            meta.Add(Ui.Tags("Tags", [.. h.Tags.Select(t => (ITag)Ui.Tag(t))]));
        }

        meta.Add(Ui.Separator());
        meta.AddRange(h.Options.Where(o => o.Key is not ("hostname" or "user" or "port" or "proxyjump" or "identityfile")).Select(o => Ui.Label(o.Key, o.Value)));

        return new ListItem(new AnonymousCommand(() => _ctx.Connect(h.Alias)) { Name = h.TunnelOnly ? "Start Tunnel" : "Connect", Result = CommandResult.Dismiss() })
        {
            Title = h.Alias,
            Subtitle = target,
            Icon = Ui.Glyph(h.SocksPort is not null ? "\uE968" : "\uE756"),
            Section = section,
            Tags = [.. tags],
            TextToSuggest = h.Alias,
            Details = new Details { Title = h.Alias, Body = h.Description is null ? string.Empty : h.Description, Metadata = [.. meta] },
            MoreCommands = [.. commands],
        };
    }
}

internal sealed partial class SftpPage : AsyncListPage
{
    private readonly SshContext _ctx;
    private readonly string _alias;
    private string? _dir;

    public SftpPage(SshContext ctx, string alias, string? dir)
    {
        _ctx = ctx;
        _alias = alias;
        _dir = dir;
        Name = dir is null ? "Browse Files (SFTP)" : "Open Folder";
        Title = $"{alias}:{dir ?? "~"}";
        Icon = Ui.Glyph("\uE8B7");
        PlaceholderText = "Filter files …";
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        var (cwd, entries) = await Sftp.List(_alias, _dir, token);
        _dir = cwd;
        Title = $"{_alias}:{cwd}";
        var folder = FolderActions(cwd);
        var items = new List<IListItem>();
        if (cwd != "/")
        {
            items.Add(new ListItem(new SftpPage(_ctx, _alias, Sftp.Parent(cwd)) { Name = "Go Up" }) { Title = "..", Subtitle = Sftp.Parent(cwd), Icon = Ui.Glyph("\uE74A"), MoreCommands = folder });
        }

        items.AddRange(entries.Select(e => e.IsDir ? DirItem(e, folder) : FileItem(e, folder)));
        return new([.. items]);
    }

    protected override ICommandItem? Empty() => Ui.EmptyItem("Empty folder", _dir ?? string.Empty, "\uE8B7");

    private ListItem DirItem(RemoteEntry e, IContextItem[] folder) => new(new SftpPage(_ctx, _alias, e.Path))
    {
        Title = e.Name,
        Subtitle = e.IsLink ? "link" : string.Empty,
        Icon = Ui.Glyph("\uE8B7"),
        Tags = [Ui.Gray(e.Modified)],
        MoreCommands = [Ui.Copy(e.Path, "Copy Path", Ui.CtrlShift(VirtualKey.C)), Rename(e), Delete(e), .. folder],
    };

    private ListItem FileItem(RemoteEntry e, IContextItem[] folder)
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        string Target() => Path.Combine(downloads, e.Name);
        return new ListItem(new AnonymousCommand(() => Status.Start($"Downloading {e.Name} …", $"Downloaded {e.Name}", async () =>
        {
            await Sftp.Download(_alias, e.Path, Target());
            Proc.Open(Target());
        }))
        { Name = "Download and Open", Result = CommandResult.KeepOpen() })
        {
            Title = e.Name,
            Subtitle = Sftp.FormatSize(e.Size),
            Icon = Ui.Glyph("\uE8A5"),
            Tags = [Ui.Gray(e.Modified)],
            MoreCommands =
            [
                new CommandContextItem(new PreviewPage(_alias, e)) { Title = "Preview", RequestedShortcut = Ui.Ctrl(VirtualKey.Y) },
                Ui.Action("Download to Downloads", "\uE896", () => Status.Start($"Downloading {e.Name} …", $"Saved to {Target()}", async () =>
                {
                    await Sftp.Download(_alias, e.Path, Target());
                    Proc.Start("explorer.exe", $"/select,{Target()}");
                }), Ui.Ctrl(VirtualKey.D)),
                Ui.Copy(e.Path, "Copy Path", Ui.CtrlShift(VirtualKey.C)),
                Rename(e),
                Delete(e),
                .. folder,
            ],
        };
    }

    private CommandContextItem Rename(RemoteEntry e)
    {
        var form = new FormBuilder().Input("name", "New Name", e.Name, required: true).Build("Rename");
        return new CommandContextItem(new FormPage($"Rename {e.Name}", form, v =>
        {
            Status.Start("Renaming …", "Renamed", () => Sftp.Rename(_alias, e.Path, Sftp.Join(Sftp.Parent(e.Path), v.Str("name"))), Reload);
            return CommandResult.GoBack();
        }))
        { Title = "Rename", RequestedShortcut = Ui.Ctrl(VirtualKey.F2) };
    }

    private CommandContextItem Delete(RemoteEntry e) =>
        Ui.Confirm("Delete", "\uE74D", $"Delete {e.Name}?", e.IsDir ? "Only empty folders can be deleted." : e.Path, () =>
            Status.Start("Deleting …", $"{e.Name} deleted", () => Sftp.Delete(_alias, e), Reload), critical: true, shortcut: Ui.Ctrl(VirtualKey.Delete));

    private IContextItem[] FolderActions(string cwd)
    {
        var mkdir = new FormBuilder().Input("name", "Folder Name", required: true).Build("Create");
        return
        [
            Ui.Action("Upload File Here", "\uE898", () => _ = Task.Run(async () =>
            {
                if (await FileDialog.Open($"Upload to {_alias}:{cwd}") is { } file)
                {
                    await Status.Run($"Uploading {Path.GetFileName(file)} …", "Uploaded", () => Sftp.Upload(_alias, file, cwd));
                    Reload();
                }
            }), Ui.CtrlShift(VirtualKey.U)),
            new CommandContextItem(new FormPage("New Folder", mkdir, v =>
            {
                Status.Start("Creating folder …", "Folder created", () => Sftp.MakeDir(_alias, Sftp.Join(cwd, v.Str("name"))), Reload);
                return CommandResult.GoBack();
            })) { Title = "New Folder", RequestedShortcut = Ui.CtrlShift(VirtualKey.N) },
            new CommandContextItem(new SftpGoTo(_ctx, _alias, cwd)) { Title = "Go to Path …", RequestedShortcut = Ui.Ctrl(VirtualKey.G) },
            Ui.Copy(cwd, "Copy Current Path"),
            Ui.Action("Refresh", "\uE72C", Reload, Ui.Ctrl(VirtualKey.R)),
            Ui.Action("Open SSH Session", "\uE756", () => _ctx.Connect(_alias)),
            Ui.Action("Open SFTP in Terminal", "\uE756", () => Terminal.Open($"sftp {_alias}", ["sftp", Terminal.SafeArg(_alias)], _ctx.Settings.TerminalChoice, _ctx.Settings.WtProfile)),
        ];
    }

    /// <summary>Lets the user type a path; the list of that folder opens as a new page.</summary>
    private sealed partial class SftpGoTo : DynamicListPage
    {
        private readonly SshContext _ctx;
        private readonly string _alias;

        public SftpGoTo(SshContext ctx, string alias, string cwd)
        {
            _ctx = ctx;
            _alias = alias;
            Name = "Go to Path";
            Title = $"Go to path on {alias}";
            PlaceholderText = cwd;
            Icon = Ui.Glyph("\uE8B7");
        }

        public override IListItem[] GetItems()
        {
            var path = SearchText.Trim();
            if (!path.StartsWith('/') && !path.StartsWith('~'))
            {
                EmptyContent = Ui.EmptyItem("Type an absolute path, e.g. /var/log, or ~ for home", string.Empty, "");
                return [];
            }

            return [new ListItem(new SftpPage(_ctx, _alias, path.StartsWith('~') ? null : path) { Name = "Open" }) { Title = $"Open {path}", Icon = Ui.Glyph("") }];
        }

        public override void UpdateSearchText(string oldSearch, string newSearch) => RaiseItemsChanged();
    }

    private sealed partial class PreviewPage(string alias, RemoteEntry entry) : AsyncMarkdownPage
    {
        protected override async Task<(string Markdown, IDetails? Details, IContextItem[] Commands)> Load(CancellationToken token)
        {
            Title = entry.Name;
            Name = "Preview";
            var text = await Sftp.Head(alias, entry);
            var binary = text.Contains('\0', StringComparison.Ordinal);
            return (binary ? "_Binary file – download it to open_" : $"```\n{text}\n```", new Details { Title = entry.Name, Metadata = [Ui.Label("Path", entry.Path), Ui.Label("Size", Sftp.FormatSize(entry.Size)), Ui.Label("Modified", entry.Modified), Ui.Label("Permissions", entry.Permissions)] }, binary ? [] : [Ui.Copy(text, "Copy Content", Ui.CtrlShift(VirtualKey.C))]);
        }
    }
}

internal sealed partial class SourcesPage : AsyncListPage
{
    private readonly SshContext _ctx;

    public SourcesPage(SshContext ctx)
    {
        _ctx = ctx;
        Id = "ssh.manage-sources";
        Name = "Manage Sources";
        Title = "SSH Sources";
        Icon = Ui.Glyph("\uE713");
        PlaceholderText = "Filter sources …";
    }

    protected override Task<Batch> Load(int page, CancellationToken token) => Task.Run(() =>
    {
        var states = _ctx.Sources.States();
        var items = new List<IListItem>();
        foreach (var s in _ctx.Sources.Sources())
        {
            var st = states.GetValueOrDefault(s.Id) ?? new SourceState();
            var tags = new List<ITag>();
            if (!s.Enabled)
            {
                tags.Add(Ui.Gray("disabled"));
            }
            else if (st.Error is not null)
            {
                tags.Add(Ui.Red("unreachable"));
            }
            else if (st.LastSuccess is not null)
            {
                tags.Add(Ui.Green($"{st.Hosts} hosts"));
            }

            if (st.Blocked > 0)
            {
                tags.Add(Ui.Orange($"{st.Blocked} blocked"));
            }

            if (st.LastCheck is { } check)
            {
                tags.Add(Ui.Tag(Ui.Ago(check)));
            }

            var commands = new List<IContextItem>
            {
                new CommandContextItem(SourceForm(s.Type, s)) { Title = "Edit Source", RequestedShortcut = Ui.Ctrl(VirtualKey.E) },
                Ui.Action("Force Full Download", "\uE896", () => Status.Start("Syncing …", "Downloaded", async () =>
                {
                    var r = await _ctx.Sources.Sync(s, true);
                    if (r.Status == "error")
                    {
                        throw new InvalidOperationException(r.State.Error);
                    }
                }, Reload)),
                new CommandContextItem(new OpenFileCommand(SshSources.CacheFile(s.Id))) { Title = "Open Cached File" },
            };
            if (st.Error is not null)
            {
                commands.Add(Ui.Copy(st.Error, "Copy Error"));
            }

            commands.Add(Ui.Confirm("Remove Source", "\uE74D", $"Remove {s.Name}?", "Its hosts disappear from the list.", () =>
            {
                _ctx.Sources.Remove(s.Id);
                Reload();
            }, critical: true));

            items.Add(new ListItem(new AnonymousCommand(() => Status.Start($"Syncing {s.Name} …", "Synced", async () =>
            {
                var r = await _ctx.Sources.Sync(s);
                if (r.Status == "error")
                {
                    throw new InvalidOperationException(r.State.Error);
                }
            }, Reload)) { Name = "Sync Now", Result = CommandResult.KeepOpen() })
            {
                Title = s.Name,
                Subtitle = s.Type switch { "onedev" => $"OneDev {s.Project} · {s.Path}", "http" => s.Url ?? string.Empty, _ => s.Path ?? string.Empty },
                Icon = Ui.Glyph(s.Type switch { "onedev" => "\uE943", "http" => "\uE774", _ => "\uE8B7" }),
                Section = "Team Sources",
                Tags = [.. tags],
                MoreCommands = [.. commands],
            });
        }

        foreach (var (type, title, glyph) in new[] { ("onedev", "Add OneDev Repository Source", "\uE943"), ("http", "Add HTTPS URL Source", "\uE774"), ("file", "Add File or Share Source", "\uE8B7") })
        {
            items.Add(new ListItem(SourceForm(type, null)) { Title = title, Icon = Ui.Glyph(glyph), Section = "Add Source" });
        }

        var (user, identity, extra) = SshConfig.ReadDefaults();
        var defaults = new FormBuilder()
            .Text("Applies to every host that does not set its own value – team hosts with a fixed user or jump host keep theirs. Stored only on this computer (~/.ssh/config.d/raycast-defaults.conf).", subtle: true)
            .Input("user", "Default User", user, "your login, e.g. jdoe")
            .Input("identity", "Identity File", identity, "e.g. ~/.ssh/id_ed25519")
            .Input("extra", "Further Options", extra, "e.g. ServerAliveInterval 30", multiline: true)
            .Build("Save");
        items.Add(new ListItem(new FormPage("Personal Defaults", defaults, v =>
        {
            SshConfig.WriteDefaults(v.Str("user"), v.Str("identity"), v.Str("extra"));
            Status.Info("Personal defaults saved");
            Reload();
            return CommandResult.GoBack();
        }))
        {
            Title = "Personal Defaults",
            Subtitle = user.Length > 0 ? $"User {user}" : "Default user and key for all hosts",
            Icon = Ui.Glyph("\uE77B"),
            Section = "This Computer",
        });

        var integrated = SshConfig.IsIntegrated();
        items.Add(new ListItem(integrated ? new OpenFileCommand(SshConfig.ConfigPath) : Ui.Confirm("Add Include Block", "\uE71B", "Add the Include block to ~/.ssh/config?", "Team hosts become available to ssh, VS Code, WinSCP etc. A backup is created first.", () =>
        {
            SshConfig.Integrate();
            Reload();
        }).Command!)
        {
            Title = "Include Block in ~/.ssh/config",
            Subtitle = integrated ? "Present – team hosts work with ssh, VS Code, WinSCP …" : "Missing – Enter adds it (backup is created)",
            Icon = Ui.Glyph(integrated ? "\uE73E" : "\uE7BA"),
            Section = "This Computer",
        });
        return new Batch([.. items]);
    });

    private FormPage SourceForm(string type, Source? source)
    {
        var form = new FormBuilder().Input("name", "Name", source?.Name ?? string.Empty, "Team Hosts", required: true).Toggle("enabled", "Enabled", source?.Enabled ?? true);
        switch (type)
        {
            case "onedev":
                form.Input("serverUrl", "Server URL", source?.ServerUrl ?? string.Empty, "https://onedev.example.com", required: true)
                    .Input("project", "Project Path", source?.Project ?? string.Empty, "it/ssh-hosts", required: true)
                    .Input("branch", "Branch", source?.Branch ?? string.Empty, "main")
                    .Input("path", "File or Folder", source?.Path ?? string.Empty, "hosts/ (folder) or config.conf")
                    .Input("token", "Access Token", placeholder: source?.Token is { Length: > 0 } ? "Stored – leave empty to keep it" : "OneDev access token", password: true)
                    .Text("Read access to the repository is enough. Only the branch head is checked on each sync; files are downloaded only when a new commit exists.", subtle: true);
                break;
            case "http":
                form.Input("url", "URL", source?.Url ?? string.Empty, "https://intranet.example.com/ssh/hosts.conf", required: true)
                    .Input("headerName", "Auth Header", source?.HeaderName ?? string.Empty, "optional, e.g. Authorization")
                    .Input("headerValue", "Header Value", placeholder: source?.HeaderValue is { Length: > 0 } ? "Stored – leave empty to keep it" : "optional", password: true)
                    .Text("Uses ETag / Last-Modified, so unchanged files are not downloaded again.", subtle: true);
                break;
            default:
                form.Input("path", "Path", source?.Path ?? string.Empty, @"\\server\share\ssh or C:\ssh\team.conf", required: true)
                    .Text("A file, or a folder whose *.conf files are combined. Only re-read when a file changed.", subtle: true);
                break;
        }

        return new FormPage(source is null ? "Add Source" : $"Edit {source.Name}", form.Build("Save and Sync"), v =>
        {
            var s = source ?? new Source { Id = SshSources.NewId(v.Str("name")), Type = type };
            s.Name = v.Str("name");
            s.Enabled = v.Bool("enabled");
            s.ServerUrl = v.Str("serverUrl");
            s.Project = v.Str("project");
            s.Branch = v.Str("branch");
            s.Path = v.Str("path");
            s.Url = v.Str("url");
            s.HeaderName = v.Str("headerName");
            if (v.Str("token") is { Length: > 0 } token)
            {
                s.Token = SshSources.Protect(token);
            }

            if (v.Str("headerValue") is { Length: > 0 } header)
            {
                s.HeaderValue = SshSources.Protect(header);
            }

            _ctx.Sources.Save(s);
            Status.Start($"Syncing {s.Name} …", $"{s.Name} saved and synced", async () =>
            {
                var r = await _ctx.Sources.Sync(s, true);
                if (r.Status == "error")
                {
                    throw new InvalidOperationException(r.State.Error);
                }
            }, Reload);
            return CommandResult.GoBack();
        }, Ui.Glyph("\uE710"));
    }
}
