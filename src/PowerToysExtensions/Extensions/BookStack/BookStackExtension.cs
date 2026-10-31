using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.BookStack;

[Guid("9F581994-065F-44D0-B6F1-A2E5F3DBFB53")]
public sealed partial class BookStackExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new BookStackProvider();
}

public sealed partial class BookStackProvider : CommandProvider
{
    public const string ProviderId = "delido.bookstack";
    private readonly ICommandItem[] _commands;

    public BookStackProvider()
    {
        var settings = new BookStackSettings();
        var api = new BookStackApi(settings);
        Id = ProviderId;
        DisplayName = "BookStack";
        Icon = Glyph.Book;
        Settings = settings.Settings;
        _commands =
        [
            new CommandItem(new SearchPage(api)) { Title = "Search BookStack", Subtitle = "Full-text search across pages, chapters, books and shelves" },
            new CommandItem(new RecentPagesPage(api)) { Title = "Recently Updated Pages", Subtitle = "BookStack" },
            new CommandItem(new BooksPage(api)) { Title = "Browse Books", Subtitle = "BookStack" },
            new CommandItem(new ShelvesPage(api)) { Title = "Browse Shelves", Subtitle = "BookStack" },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem? GetCommandItem(string id) => _commands.FirstOrDefault(c => c.Command?.Id == id);
}

internal static class Glyph
{
    public static readonly IconInfo Page = Ui.Glyph("");
    public static readonly IconInfo Chapter = Ui.Glyph("");
    public static readonly IconInfo Book = Ui.Glyph("");
    public static readonly IconInfo Shelf = Ui.Glyph("");

    public static IconInfo For(string type) => type switch
    {
        "chapter" => Chapter,
        "book" => Book,
        "bookshelf" => Shelf,
        _ => Page,
    };
}

internal static class Items
{
    public static IContextItem[] LinkActions(string url, string name) =>
        [Ui.OpenUrl(url, shortcut: Ui.Ctrl(VirtualKey.O)), .. Ui.CopyLinks(url, name)];

    public static ListItem PageItem(BookStackApi api, int id, string name, string url, string subtitle, bool draft, DateTimeOffset updated, string section = "") =>
        new(new PageDetailPage(api, id, url))
        {
            Title = name,
            Subtitle = subtitle,
            Icon = Glyph.Page,
            Section = section,
            Tags = draft ? [Ui.Yellow("Draft"), Ui.Tag(Ui.Ago(updated))] : [Ui.Tag(Ui.Ago(updated))],
            MoreCommands = LinkActions(url, name),
        };

    public static ListItem BookItem(BookStackApi api, Book book)
    {
        var url = api.BookUrl(book.Slug);
        return new ListItem(new BookContentsPage(api, book.Id, book.Name, url))
        {
            Title = book.Name,
            Subtitle = book.Description ?? string.Empty,
            Icon = Glyph.Book,
            Tags = [Ui.Tag(Ui.Ago(book.UpdatedAt))],
            MoreCommands = LinkActions(url, book.Name),
        };
    }

    public static ICommandItem? NotConfigured(BookStackApi api) =>
        api.Settings.Configured ? null : Ui.SetupItem(BookStackProvider.ProviderId, "BookStack");
}

internal sealed partial class TypeFilters : Filters
{
    public TypeFilters()
    {
        CurrentFilterId = string.Empty;
    }

    public override IFilterItem[] GetFilters() =>
    [
        new Filter { Id = string.Empty, Name = "All Types" },
        new Filter { Id = "page", Name = "Pages", Icon = Glyph.Page },
        new Filter { Id = "chapter", Name = "Chapters", Icon = Glyph.Chapter },
        new Filter { Id = "book", Name = "Books", Icon = Glyph.Book },
        new Filter { Id = "bookshelf", Name = "Shelves", Icon = Glyph.Shelf },
    ];
}

internal sealed partial class SearchPage : SearchListPage
{
    private const int PageSize = 30;
    private readonly BookStackApi _api;
    private readonly TypeFilters _filters = new();

    public SearchPage(BookStackApi api)
    {
        _api = api;
        Id = "search";
        Name = "Search";
        Title = "Search BookStack";
        Icon = Glyph.Book;
        PlaceholderText = "Search BookStack … supports [tag=value] or {updated_by:me}";
        ShowDetails = true;
        Filters = _filters;
        _filters.PropChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Filters.CurrentFilterId))
            {
                Reload();
            }
        };
    }

    protected override async Task<Batch> Search(string query, int page, CancellationToken token)
    {
        if (query.Length == 0)
        {
            return new([]);
        }

        var full = _filters.CurrentFilterId.Length > 0 ? $"{query} {{type:{_filters.CurrentFilterId}}}" : query;
        var result = await _api.Get<ListResponse<SearchResult>>("search", Http.Query(("query", full), ("page", (page + 1).ToString()), ("count", PageSize.ToString())), token);
        return new([.. result.Data.Select(Item)], (page + 1) * PageSize < result.Total);
    }

    protected override ICommandItem? Empty(string query) =>
        Items.NotConfigured(_api) ?? (query.Length == 0
            ? Ui.EmptyItem("Type to search BookStack", "Supports BookStack search syntax, e.g. [tag=value] or {updated_by:me}", "")
            : Ui.EmptyItem("No results"));

    private ListItem Item(SearchResult r)
    {
        var preview = BookStackApi.StripHtml(r.PreviewHtml?.Content);
        var location = string.Join(" › ", new[] { r.Book?.Name, r.Chapter?.Name }.Where(s => !string.IsNullOrEmpty(s)));
        ICommand command = r.Type switch
        {
            "page" => new PageDetailPage(_api, r.Id, r.Url),
            "book" => new BookContentsPage(_api, r.Id, r.Name, r.Url),
            "bookshelf" => new ShelfBooksPage(_api, r.Id, r.Name, r.Url),
            _ => new OpenUrlCommand(r.Url) { Name = "Open in Browser" },
        };

        var metadata = new List<IDetailsElement> { Ui.Label("Type", r.Type) };
        if (r.Book is not null)
        {
            metadata.Add(Ui.Label("Book", r.Book.Name));
        }

        if (r.Chapter is not null)
        {
            metadata.Add(Ui.Label("Chapter", r.Chapter.Name));
        }

        metadata.Add(Ui.Label("Updated", Ui.Date(r.UpdatedAt)));
        if (r.Tags is { Length: > 0 } tags)
        {
            metadata.Add(Ui.Tags("Tags", [.. tags.Select(t => Ui.Tag(t.ToString()))]));
        }

        var listTags = new List<ITag>();
        if (r.Draft)
        {
            listTags.Add(Ui.Yellow("Draft"));
        }

        listTags.AddRange((r.Tags ?? []).Take(2).Select(t => (ITag)Ui.Tag(t.ToString())));
        listTags.Add(Ui.Tag(Ui.Ago(r.UpdatedAt)));

        return new ListItem(command)
        {
            Title = r.Name,
            Subtitle = location.Length > 0 ? location : preview,
            Icon = Glyph.For(r.Type),
            Tags = [.. listTags],
            Details = new Details { Title = r.Name, Body = preview, Metadata = [.. metadata] },
            MoreCommands = Items.LinkActions(r.Url, r.Name),
        };
    }
}

internal sealed partial class RecentPagesPage : AsyncListPage
{
    private const int PageSize = 50;
    private readonly BookStackApi _api;

    public RecentPagesPage(BookStackApi api)
    {
        _api = api;
        Id = "recent-pages";
        Name = "Open";
        Title = "Recently Updated Pages";
        Icon = Glyph.Page;
        PlaceholderText = "Filter recently updated pages …";
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        var result = await _api.Get<ListResponse<PageListItem>>("pages", Http.Query(("sort", "-updated_at"), ("count", PageSize.ToString()), ("offset", (page * PageSize).ToString())), token);
        return new(
            [.. result.Data.Select(p => Items.PageItem(_api, p.Id, p.Name, _api.PageUrl(p.BookSlug, p.Slug), p.BookSlug, p.Draft, p.UpdatedAt))],
            (page + 1) * PageSize < result.Total);
    }

    protected override ICommandItem? Empty() => Items.NotConfigured(_api) ?? Ui.EmptyItem("No pages found");
}

internal sealed partial class BooksPage : AsyncListPage
{
    private const int PageSize = 100;
    private readonly BookStackApi _api;

    public BooksPage(BookStackApi api)
    {
        _api = api;
        Id = "browse-books";
        Name = "Open";
        Title = "Books";
        Icon = Glyph.Book;
        PlaceholderText = "Filter books …";
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        var result = await _api.Get<ListResponse<Book>>("books", Http.Query(("sort", "+name"), ("count", PageSize.ToString()), ("offset", (page * PageSize).ToString())), token);
        return new([.. result.Data.Select(b => Items.BookItem(_api, b))], (page + 1) * PageSize < result.Total);
    }

    protected override ICommandItem? Empty() => Items.NotConfigured(_api) ?? Ui.EmptyItem("No books found");
}

internal sealed partial class ShelvesPage : AsyncListPage
{
    private const int PageSize = 100;
    private readonly BookStackApi _api;

    public ShelvesPage(BookStackApi api)
    {
        _api = api;
        Id = "browse-shelves";
        Name = "Open";
        Title = "Shelves";
        Icon = Glyph.Shelf;
        PlaceholderText = "Filter shelves …";
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        var result = await _api.Get<ListResponse<Shelf>>("shelves", Http.Query(("sort", "+name"), ("count", PageSize.ToString()), ("offset", (page * PageSize).ToString())), token);
        return new(
            [.. result.Data.Select(s =>
            {
                var url = _api.ShelfUrl(s.Slug);
                return new ListItem(new ShelfBooksPage(_api, s.Id, s.Name, url))
                {
                    Title = s.Name,
                    Subtitle = s.Description ?? string.Empty,
                    Icon = Glyph.Shelf,
                    Tags = [Ui.Tag(Ui.Ago(s.UpdatedAt))],
                    MoreCommands = Items.LinkActions(url, s.Name),
                };
            })],
            (page + 1) * PageSize < result.Total);
    }

    protected override ICommandItem? Empty() => Items.NotConfigured(_api) ?? Ui.EmptyItem("No shelves found");
}

internal sealed partial class ShelfBooksPage : AsyncListPage
{
    private readonly BookStackApi _api;
    private readonly int _id;

    public ShelfBooksPage(BookStackApi api, int id, string name, string url)
    {
        _api = api;
        _id = id;
        Name = "Show Books";
        Title = name;
        Icon = Glyph.Shelf;
        PlaceholderText = $"Filter books in {name} …";
        EmptyContent = new CommandItem(new OpenUrlCommand(url)) { Title = "No books on this shelf", Icon = Glyph.Shelf };
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        var shelf = await _api.Get<ShelfDetails>($"shelves/{_id}", token: token);
        return new([.. shelf.Books.Select(b => Items.BookItem(_api, b))]);
    }
}

internal sealed partial class BookContentsPage : AsyncListPage
{
    private readonly BookStackApi _api;
    private readonly int _id;

    public BookContentsPage(BookStackApi api, int id, string name, string url)
    {
        _api = api;
        _id = id;
        Name = "Show Contents";
        Title = name;
        Icon = Glyph.Book;
        PlaceholderText = $"Filter {name} …";
        EmptyContent = new CommandItem(new OpenUrlCommand(url)) { Title = "This book is empty", Icon = Glyph.Book };
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        var book = await _api.Get<BookDetails>($"books/{_id}", token: token);
        var items = new List<IListItem>();
        foreach (var c in book.Contents.Where(c => c.Type == "page"))
        {
            items.Add(Items.PageItem(_api, c.Id, c.Name, c.Url, string.Empty, c.Draft, c.UpdatedAt, "Pages"));
        }

        foreach (var chapter in book.Contents.Where(c => c.Type == "chapter"))
        {
            if (chapter.Pages is not { Length: > 0 } pages)
            {
                items.Add(new ListItem(new OpenUrlCommand(chapter.Url) { Name = "Open in Browser" })
                {
                    Title = chapter.Name,
                    Subtitle = "Empty chapter",
                    Icon = Glyph.Chapter,
                    Section = chapter.Name,
                });
                continue;
            }

            items.AddRange(pages.Select(p => Items.PageItem(_api, p.Id, p.Name, p.Url, string.Empty, p.Draft, p.UpdatedAt, chapter.Name)));
        }

        return new([.. items]);
    }
}

internal sealed partial class PageDetailPage : AsyncMarkdownPage
{
    private readonly BookStackApi _api;
    private readonly int _id;
    private readonly string _url;

    public PageDetailPage(BookStackApi api, int id, string? url)
    {
        _api = api;
        _id = id;
        _url = url ?? api.LinkUrl(id);
        Name = "Show Page";
        Title = "Page";
        Icon = Glyph.Page;
        Commands = [Ui.OpenUrl(_url, shortcut: Ui.Ctrl(VirtualKey.O)), Ui.OpenUrl($"{_url}/edit", "Edit Page", Ui.Ctrl(VirtualKey.E))];
    }

    protected override async Task<(string Markdown, IDetails? Details, IContextItem[] Commands)> Load(CancellationToken token)
    {
        var page = await _api.Get<Page>($"pages/{_id}", token: token);
        Title = page.Name;
        var markdown = _api.PageMarkdown(page);
        var metadata = new List<IDetailsElement>
        {
            Ui.Label("Updated", Ui.Date(page.UpdatedAt)),
            Ui.Label("Updated By", page.UpdatedBy?.Name),
            Ui.Label("Created", Ui.Date(page.CreatedAt)),
            Ui.Label("Created By", page.CreatedBy?.Name),
            Ui.Label("Revisions", page.RevisionCount.ToString()),
        };
        if (page.Tags is { Length: > 0 } tags)
        {
            metadata.Add(Ui.Tags("Tags", [.. tags.Select(t => Ui.Tag(t.ToString()))]));
        }

        metadata.Add(Ui.Link("Open", _url, "Open in BookStack"));

        IContextItem[] commands =
        [
            Ui.OpenUrl(_url, shortcut: Ui.Ctrl(VirtualKey.O)),
            Ui.OpenUrl($"{_url}/edit", "Edit Page", Ui.Ctrl(VirtualKey.E)),
            Ui.Copy(markdown, "Copy Page as Markdown", Ui.Ctrl(VirtualKey.M)),
            .. Ui.CopyLinks(_url, page.Name),
        ];
        return ($"# {page.Name}\n\n{markdown}", new Details { Title = page.Name, Metadata = [.. metadata] }, commands);
    }
}
