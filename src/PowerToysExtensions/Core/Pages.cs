using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerToysExtensions.Core;

/// <summary>Result of loading one page of items.</summary>
public readonly record struct Batch(IListItem[] Items, bool HasMore = false);

/// <summary>
/// List that loads its items asynchronously (optionally page by page) and lets Command Palette filter them.
/// Errors are shown as the empty content of the list.
/// </summary>
public abstract partial class AsyncListPage : ListPage
{
    private readonly List<IListItem> _items = [];
    private CancellationTokenSource? _cts;
    private int _page;
    private bool _started;

    public override IListItem[] GetItems()
    {
        if (!_started)
        {
            Reload();
        }

        lock (_items)
        {
            return [.. _items];
        }
    }

    /// <summary>Loads everything again, e.g. after an action changed the data.</summary>
    public void Reload()
    {
        _started = true;
        _page = 0;
        _ = Fetch(reset: true);
    }

    public override void LoadMore()
    {
        if (HasMoreItems && !IsLoading)
        {
            _page++;
            _ = Fetch(reset: false);
        }
    }

    protected abstract Task<Batch> Load(int page, CancellationToken token);

    /// <summary>Shown when the list is empty without an error.</summary>
    protected virtual ICommandItem? Empty() => null;

    private async Task Fetch(bool reset)
    {
        if (reset)
        {
            _cts?.Cancel();
        }

        var cts = _cts = reset ? new CancellationTokenSource() : _cts ?? new CancellationTokenSource();
        IsLoading = true;
        try
        {
            var batch = await Load(_page, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            lock (_items)
            {
                if (reset)
                {
                    _items.Clear();
                }

                _items.AddRange(batch.Items);
            }

            HasMoreItems = batch.HasMore;
            EmptyContent = Empty();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"{GetType().Name}", ex);
            lock (_items)
            {
                _items.Clear();
            }

            HasMoreItems = false;
            EmptyContent = Ui.ErrorItem(ex, Reload);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }

        RaiseItemsChanged();
    }
}

/// <summary>
/// List that searches on the server while the user types (debounced, previous request cancelled) and loads
/// further pages on scrolling.
/// </summary>
public abstract partial class SearchListPage : DynamicListPage
{
    private readonly List<IListItem> _items = [];
    private CancellationTokenSource? _cts;
    private int _page;
    private bool _started;

    protected TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(250);

    public override IListItem[] GetItems()
    {
        if (!_started)
        {
            _started = true;
            _ = Fetch(SearchText, reset: true, delay: false);
        }

        lock (_items)
        {
            return [.. _items];
        }
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        if (oldSearch != newSearch)
        {
            _ = Fetch(newSearch, reset: true, delay: true);
        }
    }

    public override void LoadMore()
    {
        if (HasMoreItems && !IsLoading)
        {
            _page++;
            _ = Fetch(SearchText, reset: false, delay: false);
        }
    }

    public void Reload() => _ = Fetch(SearchText, reset: true, delay: false);

    /// <summary>Loads one page of results for the query (may be empty).</summary>
    protected abstract Task<Batch> Search(string query, int page, CancellationToken token);

    protected virtual ICommandItem? Empty(string query) => null;

    private async Task Fetch(string query, bool reset, bool delay)
    {
        CancellationTokenSource cts;
        if (reset)
        {
            _cts?.Cancel();
            _cts = cts = new CancellationTokenSource();
            _page = 0;
        }
        else
        {
            cts = _cts ??= new CancellationTokenSource();
        }

        IsLoading = true;
        try
        {
            if (delay)
            {
                await Task.Delay(Debounce, cts.Token);
            }

            var batch = await Search(query.Trim(), _page, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            lock (_items)
            {
                if (reset)
                {
                    _items.Clear();
                }

                _items.AddRange(batch.Items);
            }

            HasMoreItems = batch.HasMore;
            EmptyContent = Empty(query.Trim());
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error($"{GetType().Name}", ex);
            lock (_items)
            {
                _items.Clear();
            }

            HasMoreItems = false;
            EmptyContent = Ui.ErrorItem(ex, Reload);
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }

        RaiseItemsChanged();
    }
}

/// <summary>A page showing markdown with a details pane, loaded asynchronously.</summary>
public abstract partial class AsyncMarkdownPage : ContentPage
{
    private readonly MarkdownContent _content = new();
    private bool _started;

    public override IContent[] GetContent()
    {
        if (!_started)
        {
            _started = true;
            _ = Run();
        }

        return [_content];
    }

    protected abstract Task<(string Markdown, IDetails? Details, IContextItem[] Commands)> Load(CancellationToken token);

    private async Task Run()
    {
        IsLoading = true;
        try
        {
            var (markdown, details, commands) = await Load(CancellationToken.None);
            _content.Body = markdown;
            Details = details;
            if (commands.Length > 0)
            {
                Commands = commands;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"{GetType().Name}", ex);
            _content.Body = $"**Could not load:** {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }

        RaiseItemsChanged();
    }
}
