using System.Collections.Concurrent;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.BookStack;

/// <summary>
/// Details pane that shows the real page content as markdown. The page is loaded when Command Palette
/// first asks for the body; the pane updates itself when it arrives (Body raises PropChanged).
/// </summary>
internal sealed partial class PagePreview : Details
{
    private const int MaxLength = 6000;
    private static readonly SemaphoreSlim Parallel = new(3, 3);
    private static readonly ConcurrentDictionary<int, string> Cache = new();

    private readonly BookStackApi _api;
    private readonly int _id;
    private readonly string _name;
    private int _requested;

    public PagePreview(BookStackApi api, int id, string name)
    {
        _api = api;
        _id = id;
        _name = name;
        Size = ContentSize.Medium;
        base.Body = Cache.TryGetValue(id, out var cached) ? cached : "_Loading page …_";
    }

    public override string Body
    {
        get
        {
            if (!Cache.ContainsKey(_id) && Interlocked.Exchange(ref _requested, 1) == 0)
            {
                _ = Load();
            }

            return base.Body;
        }
        set => base.Body = value;
    }

    private async Task Load()
    {
        await Parallel.WaitAsync();
        try
        {
            var page = await _api.Get<Page>($"pages/{_id}");
            var markdown = _api.PageMarkdown(page).Trim();
            if (markdown.Length > MaxLength)
            {
                markdown = markdown[..MaxLength] + "\n\n_… open the page to read more_";
            }

            Cache[_id] = markdown.Length > 0 ? markdown : "_This page is empty_";
            Body = Cache[_id];
        }
        catch (Exception ex)
        {
            Log.Error($"bookstack preview {_name}", ex);
            Body = $"_Preview not available: {ex.Message}_";
        }
        finally
        {
            Parallel.Release();
        }
    }
}
