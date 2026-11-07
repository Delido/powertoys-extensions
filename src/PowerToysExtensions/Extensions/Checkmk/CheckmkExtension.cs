using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.Checkmk;

[Guid("EF36641F-2607-41D0-82D9-1DD1169D0D8C")]
public sealed partial class CheckmkExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new CheckmkProvider();
}

public sealed partial class CheckmkProvider : CommandProvider
{
    public const string ProviderId = "delido.checkmk";
    private readonly ICommandItem[] _commands;
    private readonly CommandItem _monitorItem;
    private readonly CheckmkMonitor _monitor;

    public CheckmkProvider()
    {
        var settings = new CheckmkSettings();
        var api = new CheckmkApi(settings);
        Id = ProviderId;
        DisplayName = "Checkmk";
        Icon = Ui.Glyph("");
        Settings = settings.Settings;

        _monitor = new CheckmkMonitor(api);
        _monitorItem = new CommandItem(new AnonymousCommand(() => _monitor.CheckNow()) { Id = "checkmk.monitor", Name = "Check Now", Result = CommandResult.KeepOpen() })
        {
            Title = "Checkmk Problem Monitor",
            Icon = Ui.Glyph(""),
            MoreCommands = [new CommandContextItem(Ui.OpenSettings(ProviderId)) { Title = "Monitor Settings" }],
        };
        _commands =
        [
            new CommandItem(new ProblemsPage(api)) { Title = "Show Problems", Subtitle = "Checkmk" },
            new CommandItem(new HostsPage(api)) { Title = "Search Hosts", Subtitle = "Checkmk" },
            new CommandItem(new ServicesPage(api)) { Title = "Search Services", Subtitle = "Checkmk" },
            _monitorItem,
        ];

        _monitor.StatusChanged += status => _monitorItem.Subtitle = status;
        settings.Changed += _monitor.Apply;
        _monitor.Apply();
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem? GetCommandItem(string id) => _commands.FirstOrDefault(c => c.Command?.Id == id);

    public override void Dispose()
    {
        _monitor.Dispose();
        base.Dispose();
    }
}

internal static class States
{
    public static (Tag Tag, int Order) Service(Service s) => s.HasBeenChecked == 0
        ? (Ui.Gray("PEND"), 4)
        : s.State switch
        {
            0 => (Ui.Green("OK"), 3),
            1 => (Ui.Yellow("WARN"), 2),
            2 => (Ui.Red("CRIT"), 0),
            3 => (Ui.Orange("UNKNOWN"), 1),
            _ => (Ui.Gray("PEND"), 4),
        };

    public static Tag Host(Host h) => h.HasBeenChecked == 0
        ? Ui.Gray("PEND")
        : h.State switch
        {
            0 => Ui.Green("UP"),
            1 => Ui.Red("DOWN"),
            2 => Ui.Orange("UNREACH"),
            _ => Ui.Gray("PEND"),
        };

    public static string Age(long epochSeconds)
    {
        if (epochSeconds == 0)
        {
            return string.Empty;
        }

        var seconds = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - epochSeconds);
        return seconds switch
        {
            < 60 => $"{seconds}s",
            < 3600 => $"{seconds / 60}m",
            < 86400 => $"{seconds / 3600}h",
            _ => $"{seconds / 86400}d",
        };
    }

    /// <summary>Worst state first, unhandled before handled, then by name.</summary>
    public static IEnumerable<Service> Sorted(IEnumerable<Service> services) => services
        .OrderBy(s => Service(s).Order)
        .ThenBy(s => s.Handled)
        .ThenBy(s => s.HostName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(s => s.Description, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<ITag> HandledTags(bool acknowledged, bool downtime, bool soft = false)
    {
        if (soft)
        {
            yield return Ui.Gray("SOFT");
        }

        if (acknowledged)
        {
            yield return Ui.Gray("Acknowledged");
        }

        if (downtime)
        {
            yield return Ui.Purple("Downtime");
        }
    }
}

/// <summary>Builds list items for hosts and services, with handle actions that reload the owning list.</summary>
internal sealed class ItemFactory(CheckmkApi api, Action reload)
{
    private static readonly (string Title, int Minutes)[] Downtimes = [("30 Minutes", 30), ("1 Hour", 60), ("2 Hours", 120), ("8 Hours", 480), ("1 Day", 1440), ("1 Week", 10080)];

    public ListItem Service(Service s, bool showHost = true)
    {
        var (tag, _) = States.Service(s);
        var url = api.ServiceUrl(s.HostName, s.Description);
        var longOutput = s.LongPluginOutput.Replace("\\n", "\n", StringComparison.Ordinal).Trim();
        var body = $"```\n{(s.PluginOutput.Length > 0 ? s.PluginOutput : "(no output)")}\n```" + (longOutput.Length > 0 ? $"\n\n### Details\n\n```\n{longOutput}\n```" : string.Empty);

        var commands = new List<IContextItem> { Ui.OpenUrl(url, "Open in Checkmk", Ui.Ctrl(VirtualKey.O)) };
        if (s.State != 0)
        {
            commands.AddRange(HandleActions(s.HostName, s.Description));
        }

        commands.Add(Ui.Copy(url, "Copy URL", Ui.CtrlShift(VirtualKey.C)));
        commands.Add(Ui.Copy($"{s.HostName} – {s.Description}: {tag.Text} – {s.PluginOutput}", "Copy Status Text", Ui.CtrlShift((VirtualKey)0xBE)));
        commands.Add(Ui.Copy(string.Join("\n", new[] { s.PluginOutput, longOutput }.Where(t => t.Length > 0)), "Copy Output"));
        commands.Add(Ui.Action("Refresh", "", reload, Ui.Ctrl(VirtualKey.R)));

        return new ListItem(new OpenUrlCommand(url) { Name = "Open in Checkmk" })
        {
            Title = s.Description,
            Subtitle = showHost ? s.HostName : s.PluginOutput,
            Icon = Ui.Glyph(""),
            Tags = [.. States.HandledTags(s.Acknowledged > 0, s.InDowntime, s.Soft), tag, Ui.Tag(States.Age(s.LastStateChange))],
            Details = new Details
            {
                Title = $"{s.HostName} – {s.Description}",
                Body = body,
                Metadata =
                [
                    Ui.Label("Host", s.HostName),
                    Ui.Tags("State", [tag, .. States.HandledTags(s.Acknowledged > 0, s.InDowntime, s.Soft)]),
                    Ui.Label("In State Since", States.Age(s.LastStateChange)),
                    Ui.Label("Last Check", s.LastCheck > 0 ? DateTimeOffset.FromUnixTimeSeconds(s.LastCheck).ToLocalTime().ToString("g") : null),
                    Ui.Link("Service", url, "Open service"),
                    Ui.Link("Host", api.HostUrl(s.HostName), "Open host"),
                ],
            },
            MoreCommands = [.. commands],
        };
    }

    public ListItem Host(Host h)
    {
        var url = api.HostUrl(h.Name);
        var counts = new List<ITag>();
        if (h.NumServicesCrit > 0)
        {
            counts.Add(Ui.Red($"{h.NumServicesCrit} CRIT"));
        }

        if (h.NumServicesUnknown > 0)
        {
            counts.Add(Ui.Orange($"{h.NumServicesUnknown} UNKNOWN"));
        }

        if (h.NumServicesWarn > 0)
        {
            counts.Add(Ui.Yellow($"{h.NumServicesWarn} WARN"));
        }

        var commands = new List<IContextItem> { Ui.OpenUrl(url, "Open in Checkmk", Ui.Ctrl(VirtualKey.O)) };
        commands.AddRange(HandleActions(h.Name, null));
        commands.Add(Ui.Copy(url, "Copy URL", Ui.CtrlShift(VirtualKey.C)));
        commands.Add(Ui.Copy(h.Name, "Copy Host Name", Ui.CtrlShift((VirtualKey)0xBE)));
        if (h.Address.Length > 0)
        {
            commands.Add(Ui.Copy(h.Address, "Copy IP Address"));
        }

        commands.Add(Ui.Action("Refresh", "", reload, Ui.Ctrl(VirtualKey.R)));

        return new ListItem(new HostServicesPage(api, h.Name))
        {
            Title = h.Name,
            Subtitle = string.Join(" · ", new[] { h.Alias != h.Name ? h.Alias : string.Empty, h.Address }.Where(s => s.Length > 0)),
            Icon = Ui.Glyph(""),
            Tags = [.. States.HandledTags(h.Acknowledged > 0, h.InDowntime, h.Soft), .. counts, States.Host(h)],
            Details = new Details
            {
                Title = h.Name,
                Body = $"```\n{h.PluginOutput}\n```",
                Metadata =
                [
                    Ui.Label("Alias", h.Alias),
                    Ui.Label("Address", h.Address),
                    Ui.Tags("State", [States.Host(h), .. States.HandledTags(h.Acknowledged > 0, h.InDowntime, h.Soft)]),
                    Ui.Label("In State Since", States.Age(h.LastStateChange)),
                    Ui.Label("Services", $"{h.NumServicesOk} OK · {h.NumServicesWarn} WARN · {h.NumServicesCrit} CRIT · {h.NumServicesUnknown} UNKNOWN"),
                ],
            },
            MoreCommands = [.. commands],
        };
    }

    private IEnumerable<IContextItem> HandleActions(string host, string? service)
    {
        var target = service is null ? host : $"{host} – {service}";
        var form = new FormBuilder()
            .Text(target, bold: true)
            .Input("comment", "Comment", placeholder: "Acknowledged via PowerToys", multiline: true)
            .Build("Acknowledge");
        yield return new CommandContextItem(new FormPage("Acknowledge Problem", form, inputs =>
        {
            var comment = inputs.Str("comment") is { Length: > 0 } c ? c : "Acknowledged via PowerToys";
            Status.Start("Acknowledging …", "Problem acknowledged", () => api.Acknowledge(host, service, comment), reload);
            return CommandResult.GoBack();
        }, Ui.Glyph("")))
        {
            Title = "Acknowledge Problem",
            RequestedShortcut = Ui.CtrlShift(VirtualKey.A),
        };

        yield return new CommandContextItem(new DowntimePage(api, host, service, reload))
        {
            Title = "Schedule Downtime …",
            RequestedShortcut = Ui.Ctrl(VirtualKey.D),
        };
    }

    private sealed partial class DowntimePage : ListPage
    {
        private readonly IListItem[] _items;

        public DowntimePage(CheckmkApi api, string host, string? service, Action reload)
        {
            Name = "Schedule Downtime";
            Title = $"Downtime for {(service is null ? host : $"{host} – {service}")}";
            Icon = Ui.Glyph("");
            _items =
            [
                .. Downtimes.Select(d => new ListItem(new AnonymousCommand(() =>
                    Status.Start("Scheduling downtime …", $"Downtime scheduled ({d.Title})", () => api.Downtime(host, service, d.Minutes, "Scheduled via PowerToys"), reload))
                {
                    Name = "Schedule",
                    Result = CommandResult.GoBack(),
                })
                {
                    Title = d.Title,
                    Subtitle = $"until {DateTime.Now.AddMinutes(d.Minutes):g}",
                    Icon = Ui.Glyph(""),
                }),
            ];
        }

        public override IListItem[] GetItems() => _items;
    }
}

internal sealed partial class ProblemsPage : AsyncListPage
{
    private readonly CheckmkApi _api;
    private readonly ChoiceFilters _scope = new(("unhandled", "Unhandled Problems"), ("all", "All Problems"));

    public ProblemsPage(CheckmkApi api)
    {
        _api = api;
        Id = "checkmk.problems";
        Name = "Open";
        Title = "Checkmk Problems";
        Icon = Ui.Glyph("");
        PlaceholderText = "Filter problems …";
        ShowDetails = true;
        Filters = _scope;
        _scope.Changed += Reload;
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        var problem = Q.Ne("state", "0");
        var hostsTask = _api.Hosts(problem, token);
        var servicesTask = _api.Services(problem, token);
        var all = _scope.CurrentFilterId == "all";
        var hosts = (await hostsTask).Where(h => h.HasBeenChecked > 0 && (all || !h.Handled)).ToArray();

        // Services on hosts that are down are noise in the unhandled view
        var down = hosts.Select(h => h.Name).ToHashSet();
        var services = States.Sorted((await servicesTask).Where(s => s.HasBeenChecked > 0 && (all || (!s.Handled && !down.Contains(s.HostName)))));

        var factory = new ItemFactory(_api, Reload);
        var items = new List<IListItem>();
        foreach (var h in hosts)
        {
            var item = factory.Host(h);
            item.Section = $"Hosts ({hosts.Length})";
            items.Add(item);
        }

        var serviceItems = services.Select(s => factory.Service(s)).ToList();
        serviceItems.ForEach(i => i.Section = $"Services ({serviceItems.Count})");
        items.AddRange(serviceItems);
        return new([.. items]);
    }

    protected override ICommandItem? Empty() =>
        _api.Settings.Configured ? Ui.EmptyItem("All good", "No problems right now", "") : Ui.SetupItem(CheckmkProvider.ProviderId, "Checkmk");
}

internal sealed partial class HostsPage : SearchListPage
{
    private const int MaxItems = 500;
    private readonly CheckmkApi _api;
    private readonly ChoiceFilters _filter = new(("all", "All Hosts"), ("down", "Down / Unreachable"), ("service-problems", "With Service Problems"));

    public HostsPage(CheckmkApi api)
    {
        _api = api;
        Id = "checkmk.search-hosts";
        Name = "Open";
        Title = "Checkmk Hosts";
        Icon = Ui.Glyph("");
        PlaceholderText = "Search hosts by name, alias or IP …";
        ShowDetails = true;
        Filters = _filter;
        _filter.Changed += Reload;
    }

    protected override async Task<Batch> Search(string query, int page, CancellationToken token)
    {
        var parts = new List<JsonObject>();
        if (query.Length > 0)
        {
            var regex = Q.Escape(query);
            parts.Add(Q.Or(Q.Match("name", regex), Q.Match("alias", regex), Q.Match("address", regex)));
        }

        switch (_filter.CurrentFilterId)
        {
            case "down":
                parts.Add(Q.Ne("state", "0"));
                break;
            case "service-problems":
                parts.Add(Q.Or(Q.Op(">", "num_services_crit", "0"), Q.Op(">", "num_services_unknown", "0"), Q.Op(">", "num_services_warn", "0")));
                break;
        }

        var hosts = await _api.Hosts(Q.And(parts), token);
        var factory = new ItemFactory(_api, Reload);
        return new([.. hosts.OrderByDescending(h => h.State).ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase).Take(MaxItems).Select(factory.Host)]);
    }

    protected override ICommandItem? Empty(string query) =>
        _api.Settings.Configured ? Ui.EmptyItem("No hosts found") : Ui.SetupItem(CheckmkProvider.ProviderId, "Checkmk");
}

internal sealed partial class ServicesPage : SearchListPage
{
    private const int MaxItems = 500;
    private readonly CheckmkApi _api;
    private readonly ChoiceFilters _filter = new(("all", "All Services"), ("problems", "Problems"), ("unhandled", "Unhandled Problems"), ("crit", "CRIT"), ("warn", "WARN"), ("unknown", "UNKNOWN"));

    public ServicesPage(CheckmkApi api)
    {
        _api = api;
        Id = "checkmk.search-services";
        Name = "Open";
        Title = "Checkmk Services";
        Icon = Ui.Glyph("");
        PlaceholderText = "Search services (e.g. web01 cpu) …";
        ShowDetails = true;
        Filters = _filter;
        _filter.Changed += Reload;
    }

    protected override async Task<Batch> Search(string query, int page, CancellationToken token)
    {
        var parts = _filter.CurrentFilterId switch
        {
            "problems" => [Q.Ne("state", "0")],
            "unhandled" => [Q.Ne("state", "0"), Q.Eq("acknowledged", "0"), Q.Eq("scheduled_downtime_depth", "0"), Q.Eq("host_scheduled_downtime_depth", "0")],
            "crit" => [Q.Eq("state", "2")],
            "warn" => [Q.Eq("state", "1")],
            "unknown" => [Q.Eq("state", "3")],
            _ => new List<JsonObject>(),
        };

        // Every word must match the host or the service name, so "web01 cpu" narrows down nicely
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var regex = Q.Escape(word);
            parts.Add(Q.Or(Q.Match("description", regex), Q.Match("host_name", regex)));
        }

        // Without search text and without a filter the result could be huge
        if (parts.Count == 0)
        {
            return new([]);
        }

        var services = await _api.Services(Q.And(parts), token);
        var factory = new ItemFactory(_api, Reload);
        return new([.. States.Sorted(services).Take(MaxItems).Select(s => factory.Service(s))]);
    }

    protected override ICommandItem? Empty(string query) =>
        !_api.Settings.Configured ? Ui.SetupItem(CheckmkProvider.ProviderId, "Checkmk")
        : query.Length == 0 && _filter.CurrentFilterId == "all" ? Ui.EmptyItem("Type to search services", "or pick a filter, e.g. Unhandled Problems")
        : Ui.EmptyItem("No services found");
}

internal sealed partial class HostServicesPage : AsyncListPage
{
    private readonly CheckmkApi _api;
    private readonly string _host;

    public HostServicesPage(CheckmkApi api, string host)
    {
        _api = api;
        _host = host;
        Name = "Show Services";
        Title = host;
        Icon = Ui.Glyph("");
        PlaceholderText = $"Filter services on {host} …";
        ShowDetails = true;
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        var services = await _api.Services(Q.Eq("host_name", _host), token);
        var factory = new ItemFactory(_api, Reload);
        return new([.. States.Sorted(services).Select(s => factory.Service(s, showHost: false))]);
    }
}
