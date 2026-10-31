using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.Paperless;

public sealed class PaperlessSettings : SettingsBase
{
    private readonly TextSetting _url = new("serverUrl", "Server URL", "Base URL of your Paperless-ngx server", string.Empty) { Placeholder = "https://paperless.example.com" };
    private readonly ChoiceSetSetting _autoLock = new("autoLock", "Auto-lock after", "Paperless locks again after this time without use. The API token is stored encrypted with your password.", Choices(("15 minutes", "15"), ("5 minutes", "5"), ("30 minutes", "30"), ("1 hour", "60"), ("4 hours", "240")));
    private readonly TextSetting _download = new("downloadFolder", "Download folder", "Where downloaded documents are saved (default: your Downloads folder)", string.Empty);

    public PaperlessSettings()
        : base("paperless")
    {
        Settings.Add(_url);
        Settings.Add(_autoLock);
        Settings.Add(_download);
        Load();
    }

    public string ServerUrl => Http.TrimUrl(_url.Value ?? string.Empty);

    public TimeSpan AutoLock => TimeSpan.FromMinutes(int.TryParse(_autoLock.Value, out var m) ? m : 15);

    public string DownloadFolder => string.IsNullOrWhiteSpace(_download.Value)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        : _download.Value.Trim();
}

public sealed record SearchHit(double Score, string Highlights, int Rank);

public sealed record Document(
    int Id,
    string Title,
    string? Content,
    int? Correspondent,
    int? DocumentType,
    int? StoragePath,
    int[] Tags,
    DateTimeOffset Created,
    DateTimeOffset Added,
    DateTimeOffset? Modified,
    int? ArchiveSerialNumber,
    string? OriginalFileName,
    int? PageCount,
    JsonElement[]? Notes,
    [property: System.Text.Json.Serialization.JsonPropertyName("__search_hit__")] SearchHit? SearchHit);

public sealed record PTag(int Id, string Name, string? Color, bool IsInboxTag);

public sealed record Named(int Id, string Name);

public sealed record Page<T>(int Count, string? Next, T[] Results);

public sealed record Meta(Dictionary<int, PTag> Tags, Dictionary<int, Named> Correspondents, Dictionary<int, Named> DocumentTypes, Dictionary<int, Named> StoragePaths);

/// <summary>Paperless-ngx REST API (https://docs.paperless-ngx.com/api/); the token comes from the vault.</summary>
public sealed class PaperlessApi
{
    public const int PageSize = 30;
    private readonly string _thumbs = Paths.For(Path.Combine("paperless", "thumbs"));
    private Meta? _meta;

    public PaperlessApi(PaperlessSettings settings)
    {
        Settings = settings;
        Vault = new Vault("paperless", () => settings.AutoLock);

        // remove everything cached from documents when locking
        Vault.Locked += () =>
        {
            _meta = null;
            try
            {
                Directory.Delete(_thumbs, recursive: true);
                Directory.CreateDirectory(_thumbs);
            }
            catch (IOException)
            {
                // a thumbnail is in use; removed next time
            }
        };
    }

    public PaperlessSettings Settings { get; }

    public Vault Vault { get; }

    public string DocumentUrl(int id) => $"{Settings.ServerUrl}/documents/{id}/details";

    /// <summary>Checks a token before it is stored (setup).</summary>
    public async Task Verify(string token)
    {
        if (Settings.ServerUrl.Length == 0)
        {
            throw new InvalidOperationException("Enter the server URL in the settings of this extension first");
        }

        var request = Http.Request(HttpMethod.Get, $"{Settings.ServerUrl}/api/documents/?page_size=1", new AuthenticationHeaderValue("Token", token.Trim()));
        using var _ = await Http.Send(Http.Client(), request);
    }

    public async Task<T> Get<T>(string path, CancellationToken token = default) =>
        await Http.GetJson<T>(Http.Client(), Request(HttpMethod.Get, path), token, Http.SnakeJson);

    public async Task<Meta> Meta(CancellationToken token = default)
    {
        if (_meta is not null)
        {
            return _meta;
        }

        async Task<List<T>> All<T>(string endpoint)
        {
            var items = new List<T>();
            string? next = $"{endpoint}/?page_size=1000";
            while (next is not null)
            {
                var page = await Get<Page<T>>(next, token);
                items.AddRange(page.Results);
                next = page.Next;
            }

            return items;
        }

        var tags = All<PTag>("tags");
        var correspondents = All<Named>("correspondents");
        var types = All<Named>("document_types");
        List<Named> paths;
        try
        {
            paths = await All<Named>("storage_paths");
        }
        catch (ApiException)
        {
            paths = [];
        }

        return _meta = new Meta((await tags).ToDictionary(t => t.Id), (await correspondents).ToDictionary(c => c.Id), (await types).ToDictionary(t => t.Id), paths.ToDictionary(p => p.Id));
    }

    public void ForgetMeta() => _meta = null;

    public Task<Page<Document>> Search(IEnumerable<(string Key, string? Value)> parameters, int page, CancellationToken token) =>
        Get<Page<Document>>($"documents/?{Http.Query([("page", (page + 1).ToString()), ("page_size", PageSize.ToString()), ("truncate_content", "true"), .. parameters])}", token);

    public async Task Update(int id, JsonObject changes)
    {
        var request = Request(HttpMethod.Patch, $"documents/{id}/");
        request.Content = JsonContent.Create(changes);
        using var _ = await Http.Send(Http.Client(), request);
    }

    public async Task ModifyTags(int[] ids, int[] add, int[] remove)
    {
        var request = Request(HttpMethod.Post, "documents/bulk_edit/");
        request.Content = JsonContent.Create(new { documents = ids, method = "modify_tags", parameters = new { add_tags = add, remove_tags = remove } });
        using var _ = await Http.Send(Http.Client(), request);
    }

    /// <summary>Thumbnails need the token, so they are cached as files (deleted when locking).</summary>
    public async Task<string?> Thumbnail(Document doc)
    {
        var stamp = Regex.Replace(doc.Modified?.ToString("o") ?? string.Empty, @"\D", string.Empty);
        var file = Path.Combine(_thumbs, $"{doc.Id}-{stamp[..Math.Min(14, stamp.Length)]}.webp");
        if (File.Exists(file))
        {
            return file;
        }

        try
        {
            var request = Request(HttpMethod.Get, $"documents/{doc.Id}/thumb/");
            request.Headers.Accept.Clear();
            using var response = await Http.Send(Http.Client(), request);
            await File.WriteAllBytesAsync(file, await response.Content.ReadAsByteArrayAsync());
            return file;
        }
        catch (Exception ex) when (ex is ApiException or IOException)
        {
            return null;
        }
    }

    /// <summary>Downloads the archived PDF (or the original) and returns the saved path.</summary>
    public async Task<string> Download(Document doc, bool original)
    {
        var request = Request(HttpMethod.Get, $"documents/{doc.Id}/download/{(original ? "?original=true" : string.Empty)}");
        request.Headers.Accept.Clear();
        using var response = await Http.Send(Http.Client(), request);
        var name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? $"{doc.Title}.pdf";
        name = string.Concat(Path.GetFileName(name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        Directory.CreateDirectory(Settings.DownloadFolder);
        var target = Path.Combine(Settings.DownloadFolder, name);
        for (var i = 2; File.Exists(target); i++)
        {
            target = Path.Combine(Settings.DownloadFolder, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        }

        await File.WriteAllBytesAsync(target, await response.Content.ReadAsByteArrayAsync());
        return target;
    }

    /// <summary>Uploads a file; returns the consumption task id.</summary>
    public async Task<string> Upload(string file, string? title, int? correspondent, int? type, int[] tags, DateTime? created)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(file)), "document", Path.GetFileName(file));
        if (!string.IsNullOrWhiteSpace(title))
        {
            form.Add(new StringContent(title), "title");
        }

        if (correspondent is { } c)
        {
            form.Add(new StringContent(c.ToString()), "correspondent");
        }

        if (type is { } t)
        {
            form.Add(new StringContent(t.ToString()), "document_type");
        }

        foreach (var tag in tags)
        {
            form.Add(new StringContent(tag.ToString()), "tags");
        }

        if (created is { } d)
        {
            form.Add(new StringContent(d.ToString("yyyy-MM-dd")), "created");
        }

        var request = Request(HttpMethod.Post, "documents/post_document/");
        request.Content = form;
        using var response = await Http.Send(Http.Client(), request);
        return (await response.Content.ReadAsStringAsync()).Trim('"', ' ', '\n');
    }

    public async Task<(string Status, string? Result, string? Document)> Task(string taskId)
    {
        var tasks = await Get<JsonArray>($"tasks/?task_id={Uri.EscapeDataString(taskId)}");
        var t = tasks.FirstOrDefault();
        return (t?["status"]?.GetValue<string>() ?? "PENDING", t?["result"]?.ToString(), t?["related_document"]?.ToString());
    }

    /// <summary>Turns Paperless search highlights (HTML) into markdown.</summary>
    public static string Highlights(string html) =>
        System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<span class=\"match\">(.*?)</span>", "**$1**"), "<[^>]+>", string.Empty)).Trim();

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var url = path.StartsWith("http", StringComparison.Ordinal) ? path : $"{Settings.ServerUrl}/api/{path.TrimStart('/')}";
        return Http.Request(method, url, new AuthenticationHeaderValue("Token", Vault.Secret));
    }
}
