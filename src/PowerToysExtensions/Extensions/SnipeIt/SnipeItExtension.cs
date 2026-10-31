using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.SnipeIt;

[Guid("874E31EE-457E-4C41-93E1-0FF6B4A3ED1B")]
public sealed partial class SnipeItExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new SnipeItProvider();
}

public sealed partial class SnipeItProvider : CommandProvider
{
    public const string ProviderId = "delido.snipeit";
    private readonly ICommandItem[] _commands;

    public SnipeItProvider()
    {
        var api = new SnipeItApi(new SnipeItSettings());
        Id = ProviderId;
        DisplayName = "Snipe-IT";
        Icon = Ui.Glyph("");
        Settings = api.Settings.Settings;
        _commands =
        [
            new CommandItem(new AssetsPage(api)) { Title = "Search Assets", Subtitle = "Snipe-IT" },
            new CommandItem(new LookupPage(api)) { Title = "Look up Asset", Subtitle = "Snipe-IT · by asset tag or serial (barcode scanner)" },
            new CommandItem(new UsersPage(api)) { Title = "Search Users", Subtitle = "Snipe-IT" },
            new CommandItem(new LicensesPage(api)) { Title = "Search Licenses", Subtitle = "Snipe-IT" },
            new CommandItem(new InventoryPage(api)) { Title = "Accessories & Consumables", Subtitle = "Snipe-IT" },
            new CommandItem(new AuditDuePage(api)) { Title = "Audits Due", Subtitle = "Snipe-IT" },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem? GetCommandItem(string id) => _commands.FirstOrDefault(c => c.Command?.Id == id);
}

internal static class Assets
{
    public static Tag StatusTag(StatusLabel? status, string text) => (status?.StatusMeta ?? status?.StatusType) switch
    {
        "deployed" => Ui.Blue(text),
        "deployable" => Ui.Green(text),
        "pending" => Ui.Yellow(text),
        "undeployable" => Ui.Red(text),
        "archived" => Ui.Gray(text),
        _ => Ui.Tag(text),
    };

    public static ListItem Item(SnipeItApi api, Asset a, Action reload, string section = "")
    {
        var url = api.WebUrl("hardware", a.Id);
        var tags = new List<ITag>();
        if (a.Location is not null)
        {
            tags.Add(Ui.Tag($"📍 {SnipeItApi.Decode(a.Location.Name)}"));
        }

        if (a.StatusText.Length > 0)
        {
            tags.Add(StatusTag(a.StatusLabel, a.StatusText));
        }

        return new ListItem(new AssetDetailPage(api, a, reload))
        {
            Title = a.Title,
            Subtitle = string.Join(" · ", new[] { a.AssetTag, SnipeItApi.Decode(a.Model?.Name) }.Where(s => s.Length > 0)),
            Icon = Ui.Glyph(""),
            Section = section,
            Tags = [.. tags],
            MoreCommands = Actions(api, a, reload),
        };
    }

    public static IContextItem[] Actions(SnipeItApi api, Asset a, Action reload)
    {
        var url = api.WebUrl("hardware", a.Id);
        var commands = new List<IContextItem> { Ui.OpenUrl(url, "Open in Snipe-IT", Ui.Ctrl(VirtualKey.O)) };
        if (a.CanCheckout)
        {
            commands.Add(new CommandContextItem(new CheckoutTargetPage(api, a, "user", reload)) { Title = "Check out to User …", RequestedShortcut = Ui.CtrlShift(VirtualKey.O) });
            commands.Add(new CommandContextItem(new CheckoutTargetPage(api, a, "location", reload)) { Title = "Check out to Location …" });
        }

        if (a.CanCheckin)
        {
            var form = new FormBuilder()
                .Text($"{a.AssetTag} – {a.Title}", bold: true)
                .Text($"Assigned to {a.AssignedTo?.Display}", subtle: true)
                .Input("note", "Note", placeholder: "Optional", multiline: true)
                .Build("Check in");
            commands.Add(new CommandContextItem(new FormPage($"Check in {a.AssetTag}", form, inputs =>
            {
                Status.Start("Checking in …", $"{a.AssetTag} checked in", () => api.Checkin(a.Id, inputs.Str("note")), reload);
                return CommandResult.GoBack();
            }, Ui.Glyph("")))
            {
                Title = "Check in …",
                RequestedShortcut = Ui.CtrlShift(VirtualKey.I),
            });
        }

        commands.Add(Ui.Copy(a.AssetTag, "Copy Asset Tag", Ui.CtrlShift(VirtualKey.C)));
        if (!string.IsNullOrEmpty(a.Serial))
        {
            commands.Add(Ui.Copy(a.Serial, "Copy Serial Number", Ui.CtrlShift((VirtualKey)0xBE)));
        }

        commands.Add(Ui.Copy(url, "Copy URL"));
        commands.Add(Ui.Action("Refresh", "", reload, Ui.Ctrl(VirtualKey.R)));
        return [.. commands];
    }

    public static ICommandItem? NotConfigured(SnipeItApi api) => api.Settings.Configured ? null : Ui.SetupItem(SnipeItProvider.ProviderId, "Snipe-IT");
}

internal sealed partial class AssetsPage : SearchListPage
{
    private const int PageSize = 50;
    private readonly SnipeItApi _api;
    private readonly ChoiceFilters _status = new(
        (string.Empty, "All Assets"), ("Deployed", "Deployed"), ("RTD", "Ready to Deploy"), ("Pending", "Pending"),
        ("Undeployable", "Undeployable"), ("Archived", "Archived"), ("Requestable", "Requestable"));

    public AssetsPage(SnipeItApi api)
    {
        _api = api;
        Id = "search-assets";
        Name = "Open";
        Title = "Snipe-IT Assets";
        Icon = Ui.Glyph("");
        PlaceholderText = "Search by name, tag, serial, model or user …";
        Filters = _status;
        _status.Changed += Reload;
    }

    protected override async Task<Batch> Search(string query, int page, CancellationToken token)
    {
        var result = await _api.List<Asset>(
            "hardware",
            ("search", query),
            ("status", _status.CurrentFilterId),
            ("limit", PageSize.ToString()),
            ("offset", (page * PageSize).ToString()),
            ("sort", query.Length > 0 ? null : "updated_at"),
            ("order", "desc"));
        var section = query.Length > 0 ? "Results" : "Recently Updated";
        return new([.. result.Rows.Select(a => Assets.Item(_api, a, Reload, section))], (page + 1) * PageSize < result.Total);
    }

    protected override ICommandItem? Empty(string query) => Assets.NotConfigured(_api) ?? Ui.EmptyItem("No assets found");
}

internal sealed partial class LookupPage : SearchListPage
{
    private readonly SnipeItApi _api;

    public LookupPage(SnipeItApi api)
    {
        _api = api;
        Id = "lookup-asset";
        Name = "Open";
        Title = "Look up Asset";
        Icon = Ui.Glyph("");
        PlaceholderText = "Asset tag or serial number (works with a barcode scanner)";
        Debounce = TimeSpan.FromMilliseconds(400);
    }

    protected override async Task<Batch> Search(string query, int page, CancellationToken token) =>
        query.Length == 0 ? new([]) : new([.. (await _api.Lookup(query, token)).Select(a => Assets.Item(_api, a, Reload))]);

    protected override ICommandItem? Empty(string query) =>
        Assets.NotConfigured(_api) ?? (query.Length == 0 ? Ui.EmptyItem("Scan or type an asset tag or serial number", string.Empty, "") : Ui.EmptyItem($"No asset found for “{query}”"));
}

internal sealed partial class AssetDetailPage : AsyncMarkdownPage
{
    private readonly SnipeItApi _api;
    private readonly Asset _initial;
    private readonly Action _reload;

    public AssetDetailPage(SnipeItApi api, Asset asset, Action reload)
    {
        _api = api;
        _initial = asset;
        _reload = reload;
        Name = "Show Details";
        Title = asset.AssetTag;
        Icon = Ui.Glyph("");
        Commands = Assets.Actions(api, asset, reload);
    }

    protected override async Task<(string Markdown, IDetails? Details, IContextItem[] Commands)> Load(CancellationToken token)
    {
        // the full asset includes custom fields
        var a = await _api.Get<Asset>($"hardware/{_initial.Id}", token);
        var markdown = $"# {Markdown.Escape(a.Title)}"
            + (string.IsNullOrEmpty(a.Image) ? string.Empty : $"\n\n![{a.AssetTag}]({a.Image})")
            + (string.IsNullOrEmpty(a.Notes) ? string.Empty : $"\n\n### Notes\n\n{SnipeItApi.Decode(a.Notes)}");

        var warranty = a.WarrantyExpires?.Value;
        var meta = new List<IDetailsElement>
        {
            Ui.Label("Asset Tag", a.AssetTag),
            Ui.Label("Serial", a.Serial),
            Ui.Tags("Status", Assets.StatusTag(a.StatusLabel, SnipeItApi.Decode(a.StatusLabel?.Name) is { Length: > 0 } s ? s : "–")),
        };
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                meta.Add(Ui.Label(key, value));
            }
        }

        Add("Assigned To", a.AssignedTo?.Display);
        Add("Location", SnipeItApi.Decode(a.Location?.Name));
        Add("Default Location", SnipeItApi.Decode(a.RtdLocation?.Name));
        meta.Add(Ui.Separator());
        Add("Model", SnipeItApi.Decode(a.Model?.Name));
        Add("Model No.", a.ModelNumber);
        Add("Category", SnipeItApi.Decode(a.Category?.Name));
        Add("Manufacturer", SnipeItApi.Decode(a.Manufacturer?.Name));
        Add("Company", SnipeItApi.Decode(a.Company?.Name));
        meta.Add(Ui.Separator());
        Add("Purchased", a.PurchaseDate?.ToString());
        Add("Cost", a.PurchaseCost?.ToString());
        Add("Supplier", SnipeItApi.Decode(a.Supplier?.Name));
        Add("Order No.", a.OrderNumber);
        if (a.WarrantyExpires is not null)
        {
            meta.Add(Ui.Tags("Warranty Until", warranty < DateTime.Now ? Ui.Red(a.WarrantyExpires.ToString()) : Ui.Green(a.WarrantyExpires.ToString())));
        }

        Add("Last Audit", a.LastAuditDate?.ToString());
        Add("Next Audit", a.NextAuditDate?.ToString());
        Add("Expected Check-in", a.ExpectedCheckin?.ToString());
        var fields = a.Fields.ToList();
        if (fields.Count > 0)
        {
            meta.Add(Ui.Separator());
            fields.ForEach(f => meta.Add(Ui.Label(f.Label, f.Value)));
        }

        return (markdown, new Details { Title = a.AssetTag, Metadata = [.. meta] }, Assets.Actions(_api, a, _reload));
    }
}

/// <summary>Step 1 of a check-out: pick a user or location (server search), then fill in the form.</summary>
internal sealed partial class CheckoutTargetPage : SearchListPage
{
    private readonly SnipeItApi _api;
    private readonly Asset _asset;
    private readonly string _type;
    private readonly Action _reload;

    public CheckoutTargetPage(SnipeItApi api, Asset asset, string type, Action reload)
    {
        _api = api;
        _asset = asset;
        _type = type;
        _reload = reload;
        Name = type == "user" ? "Check out to User" : "Check out to Location";
        Title = $"Check out {asset.AssetTag} to …";
        Icon = Ui.Glyph(type == "user" ? "" : "");
        PlaceholderText = type == "user" ? "Search users …" : "Search locations …";
    }

    protected override async Task<Batch> Search(string query, int page, CancellationToken token)
    {
        var rows = (await _api.List<Ref>(_type == "user" ? "users" : "locations", ("search", query), ("limit", "30"))).Rows;
        return new([.. rows.Select(target =>
        {
            var name = SnipeItApi.Decode(target.Name);
            var form = new FormBuilder()
                .Text($"{_asset.AssetTag} – {_asset.Title}", bold: true)
                .Text($"to {name}", subtle: true)
                .Date("expected", "Expected check-in")
                .Input("note", "Note", placeholder: "Optional", multiline: true)
                .Build("Check out");
            return new ListItem(new FormPage($"Check out {_asset.AssetTag}", form, inputs =>
            {
                DateTime? expected = DateTime.TryParse(inputs.Str("expected"), out var d) ? d : null;
                Status.Start("Checking out …", $"{_asset.AssetTag} checked out to {name}", () => _api.Checkout(_asset.Id, _type, target.Id, inputs.Str("note"), expected), _reload);
                return CommandResult.GoHome();
            }))
            {
                Title = name,
                Icon = Icon,
            };
        })]);
    }
}

internal sealed partial class UsersPage : SearchListPage
{
    private const int PageSize = 50;
    private readonly SnipeItApi _api;

    public UsersPage(SnipeItApi api)
    {
        _api = api;
        Id = "search-users";
        Name = "Open";
        Title = "Snipe-IT Users";
        Icon = Ui.Glyph("");
        PlaceholderText = "Search users by name, username or email …";
    }

    protected override async Task<Batch> Search(string query, int page, CancellationToken token)
    {
        var result = await _api.List<User>("users", ("search", query), ("limit", PageSize.ToString()), ("offset", (page * PageSize).ToString()), ("sort", "last_name"), ("order", "asc"));
        return new([.. result.Rows.Select(u =>
        {
            var url = _api.WebUrl("users", u.Id);
            var counts = new List<ITag>();
            if (u.AssetsCount > 0)
            {
                counts.Add(Ui.Tag($"💻 {u.AssetsCount}"));
            }

            if (u.LicensesCount > 0)
            {
                counts.Add(Ui.Tag($"🔑 {u.LicensesCount}"));
            }

            if (u.AccessoriesCount > 0)
            {
                counts.Add(Ui.Tag($"⌨ {u.AccessoriesCount}"));
            }

            var commands = new List<IContextItem> { Ui.OpenUrl(url, "Open in Snipe-IT", Ui.Ctrl(VirtualKey.O)) };
            if (!string.IsNullOrEmpty(u.Email))
            {
                commands.Add(Ui.Copy(u.Email, "Copy Email", Ui.CtrlShift(VirtualKey.C)));
            }

            if (!string.IsNullOrEmpty(u.Username))
            {
                commands.Add(Ui.Copy(u.Username, "Copy Username", Ui.CtrlShift((VirtualKey)0xBE)));
            }

            commands.Add(Ui.Copy(url, "Copy URL"));
            return new ListItem(new UserItemsPage(_api, u))
            {
                Title = SnipeItApi.Decode(u.Name),
                Subtitle = string.Join(" · ", new[] { SnipeItApi.Decode(u.Jobtitle), SnipeItApi.Decode(u.Department?.Name) }.Where(s => s.Length > 0)),
                Icon = Ui.Glyph(""),
                Tags = [.. counts],
                MoreCommands = [.. commands],
            };
        })], (page + 1) * PageSize < result.Total);
    }

    protected override ICommandItem? Empty(string query) => Assets.NotConfigured(_api) ?? Ui.EmptyItem("No users found");
}

internal sealed partial class UserItemsPage : AsyncListPage
{
    private readonly SnipeItApi _api;
    private readonly User _user;

    public UserItemsPage(SnipeItApi api, User user)
    {
        _api = api;
        _user = user;
        Name = "Show Assigned Items";
        Title = SnipeItApi.Decode(user.Name);
        Icon = Ui.Glyph("");
        PlaceholderText = $"Filter items of {Title} …";
        EmptyContent = Ui.EmptyItem("Nothing assigned", string.Empty, "");
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        async Task<T[]> RowSet<T>(string kind)
        {
            try
            {
                return (await _api.Get<RowSet<T>>($"users/{_user.Id}/{kind}", token)).Rows;
            }
            catch (ApiException)
            {
                return [];
            }
        }

        var assets = RowSet<Asset>("assets");
        var licenses = RowSet<License>("licenses");
        var accessories = RowSet<StockItem>("accessories");
        var items = new List<IListItem>();
        items.AddRange((await assets).Select(a => Assets.Item(_api, a, Reload, "Assets")));
        items.AddRange((await licenses).Select(l => new ListItem(new OpenUrlCommand(_api.WebUrl("licenses", l.Id)) { Name = "Open in Snipe-IT" })
        {
            Title = SnipeItApi.Decode(l.Name),
            Subtitle = SnipeItApi.Decode(l.Manufacturer?.Name),
            Icon = Ui.Glyph(""),
            Section = "Licenses",
        }));
        items.AddRange((await accessories).Select(s => new ListItem(new OpenUrlCommand(_api.WebUrl("accessories", s.Id)) { Name = "Open in Snipe-IT" })
        {
            Title = SnipeItApi.Decode(s.Name),
            Subtitle = SnipeItApi.Decode(s.Category?.Name),
            Icon = Ui.Glyph(""),
            Section = "Accessories",
        }));
        return new([.. items]);
    }
}

internal sealed partial class LicensesPage : SearchListPage
{
    private const int PageSize = 50;
    private readonly SnipeItApi _api;

    public LicensesPage(SnipeItApi api)
    {
        _api = api;
        Id = "search-licenses";
        Name = "Open";
        Title = "Snipe-IT Licenses";
        Icon = Ui.Glyph("");
        PlaceholderText = "Search licenses …";
        ShowDetails = true;
    }

    protected override async Task<Batch> Search(string query, int page, CancellationToken token)
    {
        var result = await _api.List<License>("licenses", ("search", query), ("limit", PageSize.ToString()), ("offset", (page * PageSize).ToString()), ("sort", "name"), ("order", "asc"));
        return new([.. result.Rows.Select(Item)], (page + 1) * PageSize < result.Total);
    }

    protected override ICommandItem? Empty(string query) => Assets.NotConfigured(_api) ?? Ui.EmptyItem("No licenses found");

    private ListItem Item(License l)
    {
        var url = _api.WebUrl("licenses", l.Id);
        var expires = l.ExpirationDate?.Value;
        var expired = expires < DateTime.Now;
        var soon = !expired && expires < DateTime.Now.AddDays(60);
        var free = l.FreeSeatsCount;
        var seats = free == 0 ? Ui.Red($"{free}/{l.Seats} free") : free <= Math.Max(1, l.Seats * 0.1) ? Ui.Orange($"{free}/{l.Seats} free") : Ui.Green($"{free}/{l.Seats} free");
        var tags = new List<ITag> { seats };
        if (expires is not null)
        {
            var text = $"{(expired ? "expired" : "expires")} {l.ExpirationDate}";
            tags.Add(expired ? Ui.Red(text) : soon ? Ui.Orange(text) : Ui.Tag(text));
        }

        var commands = new List<IContextItem> { Ui.OpenUrl(url, "Open in Snipe-IT", Ui.Ctrl(VirtualKey.O)) };

        // product_key is only returned if the user may see it
        if (!string.IsNullOrEmpty(l.ProductKey))
        {
            commands.Add(Ui.Copy(l.ProductKey, "Copy Product Key", Ui.CtrlShift(VirtualKey.C)));
        }

        commands.Add(Ui.Copy(url, "Copy URL"));
        return new ListItem(new OpenUrlCommand(url) { Name = "Open in Snipe-IT" })
        {
            Title = SnipeItApi.Decode(l.Name),
            Subtitle = SnipeItApi.Decode(l.Manufacturer?.Name),
            Icon = Ui.Glyph(""),
            Tags = [.. tags],
            Details = new Details
            {
                Title = SnipeItApi.Decode(l.Name),
                Body = string.IsNullOrEmpty(l.Notes) ? string.Empty : SnipeItApi.Decode(l.Notes),
                Metadata =
                [
                    Ui.Label("Seats", $"{free} free of {l.Seats}"),
                    Ui.Label("Expires", l.ExpirationDate?.ToString()),
                    Ui.Label("Manufacturer", SnipeItApi.Decode(l.Manufacturer?.Name)),
                    Ui.Label("Category", SnipeItApi.Decode(l.Category?.Name)),
                    Ui.Label("Licensed To", SnipeItApi.Decode(l.LicenseName)),
                    Ui.Label("License Email", l.LicenseEmail),
                    Ui.Label("Order No.", l.OrderNumber),
                ],
            },
            MoreCommands = [.. commands],
        };
    }
}

internal sealed partial class InventoryPage : AsyncListPage
{
    private readonly SnipeItApi _api;
    private readonly ChoiceFilters _view = new(("accessories", "Accessories"), ("consumables", "Consumables"), ("low", "Low Stock (all)"));

    public InventoryPage(SnipeItApi api)
    {
        _api = api;
        Id = "inventory";
        Name = "Open";
        Title = "Accessories & Consumables";
        Icon = Ui.Glyph("");
        PlaceholderText = "Filter items …";
        Filters = _view;
        _view.Changed += Reload;
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        async Task<(string Kind, StockItem Item)[]> Kind(string kind) =>
            [.. (await _api.List<StockItem>(kind, ("limit", "500"), ("sort", "name"), ("order", "asc"))).Rows.Select(r => (kind, r))];

        var rows = _view.CurrentFilterId == "low"
            ? (await Task.WhenAll(Kind("accessories"), Kind("consumables"))).SelectMany(r => r).Where(r => r.Item.Low)
            : await Kind(_view.CurrentFilterId);

        return new([.. rows.Select(r =>
        {
            var tags = new List<ITag>();
            if (r.Item.Location is not null)
            {
                tags.Add(Ui.Tag($"📍 {SnipeItApi.Decode(r.Item.Location.Name)}"));
            }

            var stock = $"{r.Item.Left} / {r.Item.Qty}";
            tags.Add(r.Item.Low ? Ui.Red(stock) : Ui.Green(stock));
            return new ListItem(new OpenUrlCommand(_api.WebUrl(r.Kind, r.Item.Id)) { Name = "Open in Snipe-IT" })
            {
                Title = SnipeItApi.Decode(r.Item.Name),
                Subtitle = string.Join(" · ", new[] { SnipeItApi.Decode(r.Item.Category?.Name), SnipeItApi.Decode(r.Item.Manufacturer?.Name) }.Where(s => s.Length > 0)),
                Icon = Ui.Glyph(r.Kind == "accessories" ? "" : ""),
                Tags = [.. tags],
                MoreCommands = [Ui.Action("Refresh", "", Reload, Ui.Ctrl(VirtualKey.R))],
            };
        })]);
    }

    protected override ICommandItem? Empty() =>
        Assets.NotConfigured(_api) ?? Ui.EmptyItem(_view.CurrentFilterId == "low" ? "Nothing is low on stock" : "No items found", string.Empty, "");
}

internal sealed partial class AuditDuePage : AsyncListPage
{
    private readonly SnipeItApi _api;

    public AuditDuePage(SnipeItApi api)
    {
        _api = api;
        Id = "audit-due";
        Name = "Open";
        Title = "Audits Due";
        Icon = Ui.Glyph("");
        PlaceholderText = "Filter assets …";
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        var overdueTask = _api.Get<RowSet<Asset>>("hardware/audit/overdue?limit=500", token);
        var dueTask = _api.Get<RowSet<Asset>>("hardware/audit/due?limit=500", token);
        var overdue = (await overdueTask).Rows;
        var ids = overdue.Select(a => a.Id).ToHashSet();
        var due = (await dueTask).Rows.Where(a => !ids.Contains(a.Id)).ToArray();
        return new([
            .. overdue.Select(a => Assets.Item(_api, a, Reload, $"Overdue ({overdue.Length})")),
            .. due.Select(a => Assets.Item(_api, a, Reload, $"Due Soon ({due.Length})"))]);
    }

    protected override ICommandItem? Empty() => Assets.NotConfigured(_api) ?? Ui.EmptyItem("No audits due", string.Empty, "");
}
