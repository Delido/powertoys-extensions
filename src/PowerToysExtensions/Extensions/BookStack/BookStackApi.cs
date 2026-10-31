using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.BookStack;

public sealed class BookStackSettings : SettingsBase
{
    private readonly TextSetting _url = new("baseUrl", "BookStack URL", "Base URL of your BookStack instance", string.Empty) { Placeholder = "https://wiki.example.com" };
    private readonly TextSetting _tokenId = new("tokenId", "Token ID", "API token ID (Edit Profile → API Tokens)", string.Empty);
    private readonly SecretSetting _tokenSecret = new("tokenSecret", "Token Secret", "API token secret (stored encrypted)");

    public BookStackSettings()
        : base("bookstack")
    {
        Settings.Add(_url);
        Settings.Add(_tokenId);
        Settings.Add(_tokenSecret);
        Load();
    }

    public string BaseUrl => Http.TrimUrl(_url.Value ?? string.Empty);

    public bool Configured => BaseUrl.Length > 0 && !string.IsNullOrWhiteSpace(_tokenId.Value) && _tokenSecret.HasValue;

    public AuthenticationHeaderValue Auth => new("Token", $"{_tokenId.Value?.Trim()}:{_tokenSecret.Value}");
}

public sealed class BookStackApi(BookStackSettings settings)
{
    public BookStackSettings Settings => settings;

    public string BaseUrl => settings.BaseUrl;

    public Task<T> Get<T>(string path, string query = "", CancellationToken token = default)
    {
        if (!settings.Configured)
        {
            throw new InvalidOperationException("BookStack is not set up yet – open the settings of this extension");
        }

        var url = $"{BaseUrl}/api/{path}{(query.Length > 0 ? "?" + query : string.Empty)}";
        return Http.GetJson<T>(Http.Client(), Http.Request(HttpMethod.Get, url, settings.Auth), token, Http.SnakeJson);
    }

    public string BookUrl(string slug) => $"{BaseUrl}/books/{slug}";

    public string ShelfUrl(string slug) => $"{BaseUrl}/shelves/{slug}";

    public string PageUrl(string bookSlug, string pageSlug) => $"{BaseUrl}/books/{bookSlug}/page/{pageSlug}";

    public string LinkUrl(int pageId) => $"{BaseUrl}/link/{pageId}";

    /// <summary>Markdown of a page; WYSIWYG pages are converted from HTML. Relative links become absolute.</summary>
    public string PageMarkdown(Page page)
    {
        var body = !string.IsNullOrWhiteSpace(page.Markdown)
            ? page.Markdown
            : Markdown.FromHtml(page.Html);
        return Regex.Replace(body, @"\]\((/[^)\s]+)", m => $"]({BaseUrl}{m.Groups[1].Value}");
    }

    public static string StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var text = Regex.Replace(html, "<[^>]+>", string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}

public sealed record ListResponse<T>(T[] Data, int Total);

public sealed record TagRef(string Name, string Value)
{
    public override string ToString() => string.IsNullOrEmpty(Value) ? Name : $"{Name}: {Value}";
}

public sealed record Ref(int Id, string Name, string Slug);

public sealed record Preview(string? Name, string? Content);

public sealed record SearchResult(
    int Id,
    string Name,
    string Slug,
    string Type,
    string Url,
    bool Draft,
    DateTimeOffset UpdatedAt,
    Ref? Book,
    Ref? Chapter,
    Preview? PreviewHtml,
    TagRef[]? Tags);

public sealed record PageListItem(int Id, string Name, string Slug, bool Draft, DateTimeOffset UpdatedAt, string BookSlug);

public sealed record Person(string Name);

public sealed record Page(
    int Id,
    string Name,
    string Slug,
    string? Html,
    string? Markdown,
    DateTimeOffset UpdatedAt,
    DateTimeOffset CreatedAt,
    int RevisionCount,
    Person? CreatedBy,
    Person? UpdatedBy,
    TagRef[]? Tags);

public sealed record Book(int Id, string Name, string Slug, string? Description, DateTimeOffset UpdatedAt);

public sealed record BookContentPage(int Id, string Name, string Slug, bool Draft, DateTimeOffset UpdatedAt, string Url);

public sealed record BookContent(int Id, string Name, string Slug, string Type, string Url, bool Draft, DateTimeOffset UpdatedAt, BookContentPage[]? Pages);

public sealed record BookDetails(int Id, string Name, string Slug, BookContent[] Contents);

public sealed record Shelf(int Id, string Name, string Slug, string? Description, DateTimeOffset UpdatedAt);

public sealed record ShelfDetails(int Id, string Name, string Slug, Book[] Books);
