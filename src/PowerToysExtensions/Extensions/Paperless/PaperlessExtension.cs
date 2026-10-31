using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.Paperless;

[Guid("3DB56E8C-615F-406D-B131-0A5909ADDAF4")]
public sealed partial class PaperlessExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new PaperlessProvider();
}

public sealed partial class PaperlessProvider : CommandProvider
{
    private readonly ICommandItem[] _commands;

    public PaperlessProvider()
    {
        var api = new PaperlessApi(new PaperlessSettings());
        var gate = new VaultGate(api.Vault, "Paperless", "API Token", "Paperless: My Profile → API Auth Token", api.Verify);
        Id = "delido.paperless";
        DisplayName = "Paperless-ngx";
        Icon = Ui.Glyph("");
        Settings = api.Settings.Settings;
        _commands =
        [
            new CommandItem(new SearchPage(api, gate)) { Title = "Search Documents", Subtitle = "Paperless-ngx" },
            new CommandItem(new InboxPage(api, gate)) { Title = "Inbox", Subtitle = "Paperless-ngx" },
            new CommandItem(new UploadPage(api, gate)) { Title = "Upload Document", Subtitle = "Paperless-ngx" },
            new CommandItem(new AnonymousCommand(() =>
            {
                api.Vault.LockNow();
                Status.Info("🔒 Paperless locked");
            })
            { Id = "paperless.lock", Name = "Lock", Result = CommandResult.Dismiss() })
            {
                Title = "Lock Paperless",
                Icon = Ui.Glyph(""),
            },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem? GetCommandItem(string id) => _commands.FirstOrDefault(c => c.Command?.Id == id);
}

internal static class Docs
{
    public static Tag ColoredTag(PTag t)
    {
        var hex = t.Color ?? string.Empty;
        return hex.Length == 7 && hex[0] == '#' && int.TryParse(hex[1..], System.Globalization.NumberStyles.HexNumber, null, out var rgb)
            ? Ui.Tag(t.Name, (byte)(rgb >> 16), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF))
            : Ui.Tag(t.Name);
    }

    public static ListItem Item(PaperlessApi api, VaultGate gate, Document doc, Meta? meta, Action reload, int[]? inboxTags = null, Action<Document>? similar = null)
    {
        var url = api.DocumentUrl(doc.Id);
        var correspondent = doc.Correspondent is { } c ? meta?.Correspondents.GetValueOrDefault(c)?.Name : null;
        var type = doc.DocumentType is { } t ? meta?.DocumentTypes.GetValueOrDefault(t)?.Name : null;
        var inInbox = inboxTags?.Any(doc.Tags.Contains) == true;

        var commands = new List<IContextItem>
        {
            Ui.Action("Download and Open", "", () => Download(api, doc, false, open: true), Ui.Ctrl(VirtualKey.O)),
        };
        if (meta is not null)
        {
            commands.Add(new CommandContextItem(EditForm(api, doc, meta, reload)) { Title = "Edit Document", RequestedShortcut = Ui.Ctrl(VirtualKey.E) });
        }

        if (inInbox && inboxTags is not null)
        {
            commands.Add(Ui.Action("Remove from Inbox", "", () => Status.Start("Updating tags …", $"Removed from inbox: {doc.Title}", () => api.ModifyTags([doc.Id], [], inboxTags), reload), Ui.CtrlShift(VirtualKey.I)));
        }

        if (similar is not null)
        {
            commands.Add(Ui.Action("Show Similar Documents", "", () => similar(doc), Ui.CtrlShift(VirtualKey.M)));
        }

        commands.Add(Ui.Action("Download Original", "", () => Download(api, doc, true, open: false)));
        commands.Add(Ui.Action("Download and Show in Folder", "", () => Download(api, doc, false, open: false)));
        commands.Add(Ui.Copy(url, "Copy Link", Ui.CtrlShift(VirtualKey.C)));
        commands.Add(Ui.Copy(doc.Title, "Copy Title", Ui.CtrlShift((VirtualKey)0xBE)));
        if (doc.ArchiveSerialNumber is { } asn)
        {
            commands.Add(Ui.Copy(asn.ToString(), "Copy ASN"));
        }

        commands.AddRange(gate.SecurityActions(reload));

        var tags = new List<ITag>();
        if (type is not null)
        {
            tags.Add(Ui.Blue(type));
        }

        tags.Add(Ui.Tag(doc.Created.ToLocalTime().ToString("d")));
        return new ListItem(new OpenUrlCommand(url) { Name = "Open in Browser" })
        {
            Title = doc.Title,
            Subtitle = correspondent ?? string.Empty,
            Icon = Ui.Glyph(inInbox ? "" : ""),
            Tags = [.. tags],
            Details = new DocumentDetails(api, doc, meta),
            MoreCommands = [.. commands],
        };
    }

    private static void Download(PaperlessApi api, Document doc, bool original, bool open) =>
        Status.Start($"Downloading {doc.Title} …", "Downloaded", async () =>
        {
            var path = await api.Download(doc, original);
            if (open)
            {
                Proc.Open(path);
            }
            else
            {
                Proc.Start("explorer.exe", $"/select,{path}");
            }
        });

    private static FormPage EditForm(PaperlessApi api, Document doc, Meta meta, Action reload)
    {
        var form = new FormBuilder()
            .Input("title", "Title", doc.Title, required: true)
            .Date("created", "Created", doc.Created.LocalDateTime)
            .Choice("correspondent", "Correspondent", [("None", string.Empty), .. meta.Correspondents.Values.OrderBy(c => c.Name).Select(c => (c.Name, c.Id.ToString()))], doc.Correspondent?.ToString() ?? string.Empty)
            .Choice("type", "Document Type", [("None", string.Empty), .. meta.DocumentTypes.Values.OrderBy(t => t.Name).Select(t => (t.Name, t.Id.ToString()))], doc.DocumentType?.ToString() ?? string.Empty)
            .Choice("tags", "Tags", meta.Tags.Values.OrderBy(t => t.Name).Select(t => (t.Name, t.Id.ToString())), string.Join(",", doc.Tags), multi: true)
            .Input("asn", "Archive Serial Number", doc.ArchiveSerialNumber?.ToString() ?? string.Empty)
            .Build("Save");
        return new FormPage($"Edit {doc.Title}", form, inputs =>
        {
            var changes = new JsonObject
            {
                ["title"] = inputs.Str("title") is { Length: > 0 } t ? t : doc.Title,
                ["created"] = inputs.Str("created") is { Length: > 0 } d ? d : doc.Created.ToString("yyyy-MM-dd"),
                ["correspondent"] = int.TryParse(inputs.Str("correspondent"), out var c) ? c : null,
                ["document_type"] = int.TryParse(inputs.Str("type"), out var dt) ? dt : null,
                ["tags"] = new JsonArray([.. inputs.Str("tags").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => (JsonNode)int.Parse(x))]),
                ["archive_serial_number"] = int.TryParse(inputs.Str("asn"), out var asn) ? asn : null,
            };
            Status.Start("Saving …", "Saved", () => api.Update(doc.Id, changes), reload);
            return CommandResult.GoBack();
        }, Ui.Glyph(""));
    }

    /// <summary>Thumbnail (needs the token, so it is downloaded) and metadata, loaded when first shown.</summary>
    private sealed partial class DocumentDetails : Details
    {
        private readonly PaperlessApi _api;
        private readonly Document _doc;
        private readonly string _snippet;
        private int _requested;

        public DocumentDetails(PaperlessApi api, Document doc, Meta? meta)
        {
            _api = api;
            _doc = doc;
            Title = doc.Title;
            Size = ContentSize.Medium;
            _snippet = doc.SearchHit?.Highlights is { Length: > 0 } h ? PaperlessApi.Highlights(h) : (doc.Content ?? string.Empty).Trim()[..Math.Min(700, (doc.Content ?? string.Empty).Trim().Length)];
            base.Body = Quote();

            var meta2 = new List<IDetailsElement> { Ui.Label("Created", doc.Created.ToLocalTime().ToString("d")) };
            if (doc.Correspondent is { } c)
            {
                meta2.Add(Ui.Label("Correspondent", meta?.Correspondents.GetValueOrDefault(c)?.Name ?? $"#{c}"));
            }

            if (doc.DocumentType is { } t)
            {
                meta2.Add(Ui.Label("Document Type", meta?.DocumentTypes.GetValueOrDefault(t)?.Name ?? $"#{t}"));
            }

            var tags = doc.Tags.Select(id => meta?.Tags.GetValueOrDefault(id)).Where(x => x is not null).Select(x => (ITag)ColoredTag(x!)).ToArray();
            if (tags.Length > 0)
            {
                meta2.Add(Ui.Tags("Tags", tags));
            }

            if (doc.StoragePath is { } sp)
            {
                meta2.Add(Ui.Label("Storage Path", meta?.StoragePaths.GetValueOrDefault(sp)?.Name ?? $"#{sp}"));
            }

            meta2.Add(Ui.Separator());
            if (doc.ArchiveSerialNumber is { } asn)
            {
                meta2.Add(Ui.Label("ASN", asn.ToString()));
            }

            if (doc.PageCount is { } pages)
            {
                meta2.Add(Ui.Label("Pages", pages.ToString()));
            }

            if (doc.Notes is { Length: > 0 } notes)
            {
                meta2.Add(Ui.Label("Notes", notes.Length.ToString()));
            }

            meta2.Add(Ui.Label("Added", doc.Added.ToLocalTime().ToString("d")));
            if (doc.OriginalFileName is { } file)
            {
                meta2.Add(Ui.Label("Original File", file));
            }

            meta2.Add(Ui.Link("Paperless", api.DocumentUrl(doc.Id), $"Document {doc.Id}"));
            Metadata = [.. meta2];
        }

        public override string Body
        {
            get
            {
                if (Interlocked.Exchange(ref _requested, 1) == 0)
                {
                    _ = Load();
                }

                return base.Body;
            }
            set => base.Body = value;
        }

        private string Quote() => _snippet.Length > 0 ? $"> {_snippet.Replace("\n", "\n> ", StringComparison.Ordinal)}" : "_No preview_";

        private async Task Load()
        {
            if (await _api.Thumbnail(_doc) is { } thumb)
            {
                Body = $"![]({new Uri(thumb).AbsoluteUri}?--x-cmdpal-maxheight=260&--x-cmdpal-fit=fit)\n\n{Quote()}";
            }
        }
    }
}

internal sealed partial class SearchPage : SearchListPage
{
    private readonly PaperlessApi _api;
    private readonly VaultGate _gate;
    private Document? _similarTo;

    public SearchPage(PaperlessApi api, VaultGate gate)
    {
        _api = api;
        _gate = gate;
        Id = "paperless.search-documents";
        Name = "Open";
        Title = "Paperless Documents";
        Icon = Ui.Glyph("");
        PlaceholderText = "Full-text search, e.g. invoice 2025 or tag:insurance";
        ShowDetails = true;
    }

    protected override async Task<Batch> Search(string query, int page, CancellationToken token)
    {
        if (_gate.Items(Reload) is { } gate)
        {
            return new(gate);
        }

        var meta = await _api.Meta(token);
        if (Filters is null)
        {
            var filters = new ChoiceFilters([
                (string.Empty, "All Documents", null),
                .. meta.DocumentTypes.Values.OrderBy(t => t.Name).Select(t => ($"type:{t.Id}", $"Type: {t.Name}", (IconInfo?)null)),
                .. meta.Tags.Values.OrderBy(t => t.Name).Select(t => ($"tag:{t.Id}", $"Tag: {t.Name}", (IconInfo?)null)),
                .. meta.Correspondents.Values.OrderBy(c => c.Name).Select(c => ($"corr:{c.Id}", $"Correspondent: {c.Name}", (IconInfo?)null))]);
            filters.Changed += Reload;
            Filters = filters;
        }

        if (query.Length > 0)
        {
            _similarTo = null;
        }

        var filter = (Filters as ChoiceFilters)?.CurrentFilterId ?? string.Empty;
        var parameters = new List<(string, string?)>();
        if (_similarTo is not null)
        {
            parameters.Add(("more_like_id", _similarTo.Id.ToString()));
        }
        else
        {
            parameters.Add(query.Length > 0 ? ("query", query) : ("ordering", "-created"));
            var parts = filter.Split(':');
            if (parts.Length == 2)
            {
                parameters.Add((parts[0] switch { "type" => "document_type__id", "tag" => "tags__id__all", _ => "correspondent__id" }, parts[1]));
            }
        }

        Title = _similarTo is null ? "Paperless Documents" : $"Similar to {_similarTo.Title}";
        var result = await _api.Search(parameters, page, token);
        var section = query.Length > 0 || _similarTo is not null ? "Results" : "Newest Documents";
        return new([.. result.Results.Select(d =>
        {
            var item = Docs.Item(_api, _gate, d, meta, Reload, similar: doc =>
            {
                _similarTo = doc;
                Reload();
            });
            item.Section = section;
            return item;
        })], result.Next is not null);
    }

    protected override ICommandItem? Empty(string query) =>
        _api.Settings.ServerUrl.Length == 0 ? Ui.SetupItem("delido.paperless", "Paperless (server URL)") : Ui.EmptyItem(query.Length > 0 ? "No documents found" : "No documents");
}

internal sealed partial class InboxPage : AsyncListPage
{
    private readonly PaperlessApi _api;
    private readonly VaultGate _gate;

    public InboxPage(PaperlessApi api, VaultGate gate)
    {
        _api = api;
        _gate = gate;
        Id = "paperless.inbox";
        Name = "Open";
        Title = "Paperless Inbox";
        Icon = Ui.Glyph("");
        PlaceholderText = "Filter inbox …";
        ShowDetails = true;
        MaxAge = TimeSpan.FromSeconds(30);
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        if (_gate.Items(Reload) is { } gate)
        {
            return new(gate);
        }

        var meta = await _api.Meta(token);
        var inbox = meta.Tags.Values.Where(t => t.IsInboxTag).Select(t => t.Id).ToArray();
        if (inbox.Length == 0)
        {
            return new([new ListItem(new NoOpCommand()) { Title = "No inbox tag", Subtitle = "Mark a tag as inbox tag in Paperless", Icon = Ui.Glyph("") }]);
        }

        var result = await _api.Search([("tags__id__in", string.Join(",", inbox)), ("ordering", "-added")], page, token);
        return new([.. result.Results.Select(d => Docs.Item(_api, _gate, d, meta, Reload, inbox))], result.Next is not null);
    }

    protected override ICommandItem? Empty() => Ui.EmptyItem("Inbox is empty", "All documents are processed", "");
}

/// <summary>Pick a file (newest downloads or the Windows Open dialog), then title, tags etc. in a form.</summary>
internal sealed partial class UploadPage : AsyncListPage
{
    private static readonly string[] Types = [".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".webp", ".gif", ".txt", ".docx", ".odt", ".eml"];
    private readonly PaperlessApi _api;
    private readonly VaultGate _gate;

    public UploadPage(PaperlessApi api, VaultGate gate)
    {
        _api = api;
        _gate = gate;
        Id = "paperless.upload-document";
        Name = "Open";
        Title = "Upload Document";
        Icon = Ui.Glyph("");
        PlaceholderText = "Pick a file to upload …";
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        if (_gate.Items(Reload) is { } gate)
        {
            return new(gate);
        }

        var meta = await _api.Meta(token);
        var items = new List<IListItem>
        {
            new ListItem(new AnonymousCommand(() => _ = Task.Run(async () =>
            {
                var file = await FileDialog.Open("Upload to Paperless", "Documents\0*.pdf;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.webp;*.gif;*.txt;*.docx;*.odt;*.eml\0All files\0*.*\0");
                if (file is not null)
                {
                    Status.Info($"Selected {Path.GetFileName(file)} – it is listed at the top now");
                    _picked = file;
                    Reload();
                }
            }))
            { Name = "Choose File", Result = CommandResult.KeepOpen() })
            {
                Title = "Choose File …",
                Subtitle = "Opens the Windows file dialog",
                Icon = Ui.Glyph(""),
                Section = "File",
            },
        };

        var folder = _api.Settings.DownloadFolder;
        var files = Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles().Where(f => Types.Contains(f.Extension.ToLowerInvariant())).OrderByDescending(f => f.LastWriteTime).Take(15).Select(f => f.FullName)
            : [];
        if (_picked is not null)
        {
            files = files.Where(f => f != _picked).Prepend(_picked);
        }

        items.AddRange(files.Select(f => new ListItem(Form(f, meta))
        {
            Title = Path.GetFileName(f),
            Subtitle = f == _picked ? "Chosen file" : $"{new FileInfo(f).LastWriteTime:g}",
            Icon = new IconInfo(f),
            Section = f == _picked ? "File" : "Recent Downloads",
        }));
        return new([.. items]);
    }

    private string? _picked;

    private FormPage Form(string file, Meta meta)
    {
        var form = new FormBuilder()
            .Text(Path.GetFileName(file), bold: true)
            .Input("title", "Title", placeholder: "Optional – Paperless uses the file name otherwise")
            .Choice("correspondent", "Correspondent", [("Auto", string.Empty), .. meta.Correspondents.Values.OrderBy(c => c.Name).Select(c => (c.Name, c.Id.ToString()))])
            .Choice("type", "Document Type", [("Auto", string.Empty), .. meta.DocumentTypes.Values.OrderBy(t => t.Name).Select(t => (t.Name, t.Id.ToString()))])
            .Choice("tags", "Tags", meta.Tags.Values.OrderBy(t => t.Name).Select(t => (t.Name, t.Id.ToString())), multi: true)
            .Date("created", "Created")
            .Text("Paperless processes the document in the background; you get a notification when it is done.", subtle: true)
            .Build("Upload");
        return new FormPage($"Upload {Path.GetFileName(file)}", form, inputs =>
        {
            var tags = inputs.Str("tags").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            int? correspondent = int.TryParse(inputs.Str("correspondent"), out var c) ? c : null;
            int? type = int.TryParse(inputs.Str("type"), out var t) ? t : null;
            DateTime? created = DateTime.TryParse(inputs.Str("created"), out var d) ? d : null;
            Status.Start($"Uploading {Path.GetFileName(file)} …", "Uploaded – Paperless is processing it", async () =>
            {
                var task = await _api.Upload(file, inputs.Str("title"), correspondent, type, tags, created);
                _ = Watch(task, Path.GetFileName(file));
            });
            return CommandResult.GoBack();
        }, Ui.Glyph(""));
    }

    /// <summary>Follows the consumption task and notifies when the document is ready (or failed).</summary>
    private async Task Watch(string taskId, string name)
    {
        for (var i = 0; i < 120; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            try
            {
                var (status, result, document) = await _api.Task(taskId);
                if (status == "SUCCESS")
                {
                    Notify.Show("📄 Document added", name, int.TryParse(document, out var id) ? _api.DocumentUrl(id) : null, "paperless");
                    return;
                }

                if (status is "FAILURE" or "REVOKED")
                {
                    Notify.Show("Paperless could not add the document", $"{name}: {result}", group: "paperless");
                    return;
                }
            }
            catch (VaultLockedException)
            {
                return;
            }
            catch (ApiException)
            {
                // try again
            }
        }
    }
}
