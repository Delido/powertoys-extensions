using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.SharePoint;

[Guid("AAAADC85-C95B-4980-AC50-5A59BC1DE2F3")]
public sealed partial class SharePointExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new SharePointProvider();
}

public sealed class SharePointSettings : SettingsBase
{
    private readonly TextSetting _tenant = new("tenantId", "Tenant ID", "Directory (tenant) ID of your Microsoft 365 tenant (Entra ID → Overview); empty = any work account", string.Empty);
    private readonly TextSetting _client = new("clientId", "Client ID", "Application (client) ID of the Entra ID app registration (redirect URI ms-appx-web://microsoft.aad.brokerplugin/<client id>)", string.Empty);
    private readonly TextSetting _scope = new("scopeQuery", "Limit search to", "Optional KQL restriction or a site URL, e.g. path:\"https://contoso.sharepoint.com/sites/Docs\"", string.Empty);

    public SharePointSettings()
        : base("sharepoint")
    {
        Settings.Add(_tenant);
        Settings.Add(_client);
        Settings.Add(_scope);
        Load();
    }

    public string TenantId => string.IsNullOrWhiteSpace(_tenant.Value) ? "organizations" : _tenant.Value.Trim();

    public string ClientId => _client.Value ?? string.Empty;

    /// <summary>KQL restriction; a plain site URL becomes path:"url".</summary>
    public string ScopeRestriction
    {
        get
        {
            var value = _scope.Value?.Trim() ?? string.Empty;
            return value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? $"path:\"{value.TrimEnd('/')}\"" : value;
        }
    }
}

public sealed partial class SharePointProvider : CommandProvider
{
    public const string ProviderId = "delido.sharepoint";
    private readonly ICommandItem[] _commands;

    public SharePointProvider()
    {
        var settings = new SharePointSettings();

        // Delegated, read-only: results are always limited to what the signed-in user can access.
        var graph = new Graph(new MicrosoftAuth("sharepoint", () => settings.TenantId, () => settings.ClientId, ["https://graph.microsoft.com/User.Read", "https://graph.microsoft.com/Files.Read.All", "https://graph.microsoft.com/Sites.Read.All"]));
        Id = ProviderId;
        DisplayName = "SharePoint";
        Icon = Glyphs.Site;
        Settings = settings.Settings;
        _commands = [new CommandItem(new SearchPage(graph, settings)) { Title = "Search SharePoint", Subtitle = "Full-text search across SharePoint documents, pages and sites" }];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem? GetCommandItem(string id) => _commands.FirstOrDefault(c => c.Command?.Id == id);
}

internal static class Glyphs
{
    public static readonly IconInfo Site = Ui.Glyph("");
    public static readonly IconInfo Page = Ui.Glyph("");
    public static readonly IconInfo Document = Ui.Glyph("");
    public static readonly IconInfo Sheet = Ui.Glyph("");
    public static readonly IconInfo Slides = Ui.Glyph("");
    public static readonly IconInfo Pdf = Ui.Glyph("");

    public static IconInfo For(string extension) => extension switch
    {
        "aspx" => Page,
        "xlsx" or "xls" or "xlsm" or "csv" => Sheet,
        "pptx" or "ppt" or "pptm" => Slides,
        "pdf" => Pdf,
        _ => Document,
    };
}

/// <summary>One Microsoft Search request; Graph allows only one entity type combination per request.</summary>
internal sealed record SearchRequest(string EntityType, string Kql, string[]? Fields = null, Func<string, bool>? UrlFilter = null);

internal sealed partial class SearchPage : SearchListPage
{
    private const int PageSize = 25;

    // Site pages are indexed as list items of the "Site Pages" library, documents as drive items.
    // The index also files page asset folders under this class; real pages end in .aspx.
    private static readonly SearchRequest Pages = new(
        "listItem",
        "contentclass:STS_ListItem_WebPageLibrary",
        ["title", "webUrl", "lastModifiedDateTime", "lastModifiedBy", "createdBy", "createdDateTime"],
        url => url.Split('?', '#')[0].EndsWith(".aspx", StringComparison.OrdinalIgnoreCase));

    private static readonly Dictionary<string, SearchRequest[]> Requests = new()
    {
        ["pages"] = [Pages],
        ["all"] = [new("driveItem", string.Empty), Pages],
        ["documents"] = [new("driveItem", "-filetype:aspx")],
        ["word"] = [new("driveItem", "(filetype:docx OR filetype:doc)")],
        ["pdf"] = [new("driveItem", "filetype:pdf")],
        ["excel"] = [new("driveItem", "(filetype:xlsx OR filetype:xls)")],
        ["powerpoint"] = [new("driveItem", "(filetype:pptx OR filetype:ppt)")],
        ["sites"] = [new("site", string.Empty)],
    };

    private static readonly Dictionary<string, string> OfficeSchemes = new()
    {
        ["doc"] = "ms-word",
        ["docx"] = "ms-word",
        ["docm"] = "ms-word",
        ["xls"] = "ms-excel",
        ["xlsx"] = "ms-excel",
        ["xlsm"] = "ms-excel",
        ["ppt"] = "ms-powerpoint",
        ["pptx"] = "ms-powerpoint",
        ["pptm"] = "ms-powerpoint",
        ["vsdx"] = "ms-visio",
    };

    private readonly Graph _graph;
    private readonly SharePointSettings _settings;
    private readonly ChoiceFilters _filters = new(("pages", "Pages"), ("all", "Documents & Pages"), ("documents", "Documents"), ("word", "Word"), ("pdf", "PDF"), ("excel", "Excel"), ("powerpoint", "PowerPoint"), ("sites", "Sites"));

    public SearchPage(Graph graph, SharePointSettings settings)
    {
        _graph = graph;
        _settings = settings;
        Id = "search";
        Name = "Search";
        Title = "Search SharePoint";
        Icon = Glyphs.Site;
        PlaceholderText = "Search SharePoint … supports KQL, e.g. author:\"Jane Doe\" or \"exact phrase\"";
        ShowDetails = true;
        Filters = _filters;
        _filters.Changed += Reload;
    }

    protected override async Task<Batch> Search(string query, int page, CancellationToken token)
    {
        if (query.Length == 0 || !_graph.Auth.Configured)
        {
            return new([]);
        }

        var requests = Requests[_filters.CurrentFilterId];
        var prefixed = Graph.PrefixQuery(query);
        var restriction = _settings.ScopeRestriction;
        var results = await Task.WhenAll(requests.Select(r =>
        {
            var full = string.Join(' ', new[] { prefixed, r.Kql }.Where(s => s.Length > 0));
            return _graph.Search(r.EntityType, restriction.Length > 0 ? $"({full}) {restriction}" : full, page * PageSize, PageSize, r.Fields, token);
        }));

        // interleave so that each source contributes its best hits first, without duplicates
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<IListItem>();
        for (var i = 0; i < results.Max(r => r.Hits.Count); i++)
        {
            for (var source = 0; source < results.Length; source++)
            {
                if (i >= results[source].Hits.Count || results[source].Hits[i] is not { } hit)
                {
                    continue;
                }

                var url = Graph.Str(hit["resource"]?["webUrl"]);
                if ((requests[source].UrlFilter is { } filter && !filter(url)) || !seen.Add(NormalizeUrl(url)))
                {
                    continue;
                }

                items.Add(Graph.Str(hit["resource"]?["@odata.type"]) == "#microsoft.graph.site" ? SiteItem(hit) : DocumentItem(hit));
            }
        }

        return new([.. items], results.Any(r => r.HasMore));
    }

    protected override ICommandItem? Empty(string query) =>
        !_graph.Auth.Configured
            ? Ui.SetupItem(SharePointProvider.ProviderId, "SharePoint (Client ID of the app registration)")
            : query.Length == 0
                ? Ui.EmptyItem("Type to search SharePoint", "Supports KQL, e.g. author:\"Jane Doe\" or \"exact phrase\"", "")
                : Ui.EmptyItem("No results");

    private ListItem DocumentItem(JsonNode hit)
    {
        var item = hit["resource"]!;
        var url = Graph.Str(item["webUrl"]);
        var name = ItemName(item, url);
        var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        var title = extension == "aspx" ? name[..^5] : name;
        var location = LocationFromUrl(url);
        var modifiedBy = Graph.Str(item["lastModifiedBy"]?["user"]?["displayName"]);
        var createdBy = Graph.Str(item["createdBy"]?["user"]?["displayName"]);
        var modified = Graph.Time(item["lastModifiedDateTime"]);
        var size = item["size"]?.GetValue<long>();

        var metadata = new List<IDetailsElement> { Ui.Label("Location", location.Length > 0 ? location : "–") };
        if (modifiedBy.Length > 0)
        {
            metadata.Add(Ui.Label("Modified By", modifiedBy));
        }

        metadata.Add(Ui.Label("Modified", Ui.Date(modified)));
        if (createdBy.Length > 0)
        {
            metadata.Add(Ui.Label("Created By", createdBy));
        }

        if (size is { } bytes)
        {
            metadata.Add(Ui.Label("Size", FormatSize(bytes)));
        }

        var summary = Graph.Summary(Graph.Str(hit["summary"]), markdown: true);
        var commands = new List<IContextItem>();
        if (OfficeSchemes.TryGetValue(extension, out var scheme))
        {
            commands.Add(Ui.OpenUrl($"{scheme}:ofe|u|{url}", "Open in Desktop App", Ui.Ctrl(VirtualKey.O)));
        }

        commands.AddRange(Ui.CopyLinks(url, title));
        commands.Add(_graph.SignOutAction(Reload));

        return new ListItem(new OpenUrlCommand(url) { Name = "Open in Browser", Result = CommandResult.Dismiss() })
        {
            Title = title,
            Subtitle = location,
            Icon = Glyphs.For(extension),
            Tags = modified is null ? [] : [Ui.Tag(Ui.Ago(modified))],
            Details = new Details
            {
                Title = title,
                Body = summary.Length > 0 ? summary : "_No preview available_",
                Metadata = [.. metadata],
                Size = ContentSize.Medium,
            },
            MoreCommands = [.. commands],
        };
    }

    private ListItem SiteItem(JsonNode hit)
    {
        var site = hit["resource"]!;
        var url = Graph.Str(site["webUrl"]);
        var title = new[] { Graph.Str(site["displayName"]), Graph.Str(site["name"]), url }.First(s => s.Length > 0);
        var description = Graph.Str(site["description"]);
        return new ListItem(new OpenUrlCommand(url) { Name = "Open in Browser", Result = CommandResult.Dismiss() })
        {
            Title = title,
            Subtitle = description.Length > 0 ? description : url,
            Icon = Glyphs.Site,
            Details = new Details
            {
                Title = title,
                Body = description,
                Metadata = [Ui.Link("Site", url), Ui.Label("Modified", Ui.Date(Graph.Time(site["lastModifiedDateTime"])))],
                Size = ContentSize.Medium,
            },
            MoreCommands = [.. Ui.CopyLinks(url, title), _graph.SignOutAction(Reload)],
        };
    }

    /// <summary>Pages are shown by their title; list item hits have no file name, so it comes from the URL.</summary>
    private static string ItemName(JsonNode item, string url)
    {
        if (Graph.Str(item["fields"]?["title"]) is { Length: > 0 } pageTitle)
        {
            return $"{pageTitle}.aspx";
        }

        if (Graph.Str(item["name"]) is { Length: > 0 } name)
        {
            return name;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Uri.UnescapeDataString(uri.AbsolutePath.Split('/')[^1]) : url;
    }

    /// <summary>"Site › Library › Folder" from a SharePoint document URL.</summary>
    private static string LocationFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var parts = Uri.UnescapeDataString(uri.AbsolutePath).Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1).ToList();
        if (parts.Count > 0 && parts[0] is "sites" or "teams" or "personal")
        {
            parts.RemoveAt(0);
        }

        return string.Join(" › ", parts);
    }

    private static string NormalizeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Uri.UnescapeDataString($"{uri.Host}{uri.AbsolutePath}").ToLowerInvariant() : url.ToLowerInvariant();

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB",
    };
}
