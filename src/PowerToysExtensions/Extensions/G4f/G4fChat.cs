using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.G4f;

/// <summary>
/// AI Chat: type in the search box and press Enter to send. The chat dropdown switches chats; messages
/// update in place while the answer streams in (rebuilding the list would jump to the top).
/// </summary>
public sealed partial class ChatPage : DynamicListPage
{
    private static readonly (string Title, string Value)[] CreativityChoices = [("None (0.0)", "0"), ("Low (0.3)", "0.3"), ("Medium (0.7)", "0.7"), ("High (1.0)", "1"), ("Very high (1.5)", "1.5")];

    private readonly G4fContext _context;
    private readonly Func<(string Prompt, string Answer, List<string> Files, ModelRef? Model)>? _seed;
    private readonly Dictionary<long, (ListItem Item, Details Details)> _items = [];
    private readonly List<string> _pending = [];
    private readonly ListItem _sendItem;
    private CancellationTokenSource? _cts;
    private bool _seeded;
    private string _filtersFor = string.Empty;

    public ChatPage(G4fContext context, Func<(string Prompt, string Answer, List<string> Files, ModelRef? Model)>? seed = null)
    {
        _context = context;
        _seed = seed;
        Id = seed is null ? "g4f.ai-chat" : string.Empty;
        Name = "Open";
        Title = "AI Chat";
        Icon = Ui.Glyph("");
        PlaceholderText = "Ask anything … (Enter sends)";
        ShowDetails = true;
        _sendItem = new ListItem(new AnonymousCommand(Send) { Name = "Send", Result = CommandResult.KeepOpen() }) { Icon = Ui.Glyph("") };
    }

    private G4fState State => _context.Store.State;

    private Chat Current
    {
        get
        {
            var chat = State.Chats.FirstOrDefault(c => c.Id == State.CurrentChat);
            if (chat is null)
            {
                chat = State.Chats.FirstOrDefault() ?? NewChat();
                State.CurrentChat = chat.Id;
            }

            return chat;
        }
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        if (string.IsNullOrWhiteSpace(oldSearch) != string.IsNullOrWhiteSpace(newSearch))
        {
            RaiseItemsChanged();
        }
        else
        {
            _sendItem.Title = $"Send: {newSearch.Trim()}";
        }
    }

    public override IListItem[] GetItems()
    {
        if (_seed is not null && !_seeded)
        {
            _seeded = true;
            var (prompt, answer, files, model) = _seed();
            var chat = NewChat(model);
            chat.Messages.Add(new ChatMessage { Prompt = prompt, Answer = answer, Files = files, Finished = true });
            chat.Name = Shorten(prompt, 40);
            _context.Store.Save();
        }

        var current = Current;
        Title = $"AI Chat · {current.Name}";
        UpdateFilters();
        var items = new List<IListItem>();
        if (_context.Store.Find(current.Model) is null && _context.Store.ModelFor() is null)
        {
            return [G4fContext.NoModels()];
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            _sendItem.Title = $"Send: {SearchText.Trim()}";
            _sendItem.Subtitle = $"{ModelOf(current)?.Label}{(_pending.Count > 0 ? $" · 📎 {_pending.Count} file(s)" : string.Empty)}";
            _sendItem.MoreCommands = ChatActions();
            items.Add(_sendItem);
        }

        if (_pending.Count > 0)
        {
            items.Add(new ListItem(new AnonymousCommand(() =>
            {
                _pending.Clear();
                RaiseItemsChanged();
            }) { Name = "Remove Files", Result = CommandResult.KeepOpen() })
            {
                Title = $"📎 {string.Join(", ", _pending.Select(Path.GetFileName))}",
                Subtitle = "Attached to the next message · Enter removes them",
                Icon = Ui.Glyph(""),
            });
        }

        items.AddRange(current.Messages.AsEnumerable().Reverse().Select(m => (IListItem)Item(current, m)));
        if (items.Count == 0)
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = "Type a message and press Enter",
                Subtitle = $"{ModelOf(current)?.Label} · Ctrl+K for chat actions",
                Icon = Ui.Glyph(""),
                MoreCommands = ChatActions(),
            });
        }

        return [.. items];
    }

    private ModelRef? ModelOf(Chat chat) => _context.Store.Find(chat.Model) ?? _context.Store.ModelFor("ai-chat");

    private Chat NewChat(ModelRef? model = null, AiPreset? preset = null)
    {
        preset ??= State.Presets.FirstOrDefault(p => p.IsDefault);
        var chat = new Chat
        {
            Model = model?.Key ?? preset?.Model ?? string.Empty,
            Creativity = preset?.Creativity ?? 0.7,
            SystemPrompt = preset?.SystemPrompt ?? string.Empty,
        };
        State.Chats.Insert(0, chat);
        State.CurrentChat = chat.Id;
        return chat;
    }

    private void UpdateFilters()
    {
        var chats = State.Chats.OrderByDescending(c => c.Pinned).ThenByDescending(c => c.Created).ToArray();
        var key = string.Join('|', chats.Select(c => $"{c.Id}:{c.Name}:{c.Pinned}")) + State.CurrentChat;
        if (key == _filtersFor)
        {
            return;
        }

        _filtersFor = key;
        var filters = new ChoiceFilters([.. chats.Select(c => (c.Id, $"{(c.Pinned ? "📌 " : string.Empty)}{c.Name}", (IconInfo?)null))]);
        if (chats.Length > 0)
        {
            filters.CurrentFilterId = State.CurrentChat;
        }

        filters.Changed += () =>
        {
            if (filters.CurrentFilterId != State.CurrentChat)
            {
                Cancel();
                State.CurrentChat = filters.CurrentFilterId;
                _context.Store.Save();
                RaiseItemsChanged();
            }
        };
        Filters = filters;
    }

    private ListItem Item(Chat chat, ChatMessage m)
    {
        if (!_items.TryGetValue(m.Id, out var entry))
        {
            var details = new Details { Size = ContentSize.Large };
            var item = new ListItem(new AnonymousCommand(() => ClipboardHelper.SetText(m.Answer)) { Name = "Copy Response", Result = CommandResult.ShowToast("Response copied") })
            {
                Icon = Ui.Glyph(""),
                Details = details,
            };
            _items[m.Id] = entry = (item, details);
        }

        Fill(entry.Item, entry.Details, m);
        entry.Item.MoreCommands = MessageActions(chat, m);
        return entry.Item;
    }

    private static void Fill(ListItem item, Details details, ChatMessage m)
    {
        item.Title = Shorten(m.Prompt, 120);
        item.Subtitle = Shorten(Graph.StripHtml(m.Answer).Replace('\n', ' '), 140);
        item.Tags = m.Finished ? [Ui.Tag(m.Created.ToString("t", CultureInfo.CurrentCulture))] : [Ui.Blue("…")];
        details.Title = Shorten(m.Prompt, 80);
        var files = m.Files.Count > 0 ? $"\n\n📎 {string.Join(", ", m.Files.Select(Path.GetFileName))}" : string.Empty;
        details.Body = $"**You:** {m.Prompt}{files}\n\n---\n\n{(m.Answer.Length > 0 ? m.Answer : "_…_")}";
    }

    private static string Shorten(string text, int length)
    {
        var line = text.Trim().Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ');
        return line.Length > length ? line[..length] + "…" : line;
    }

    private void Send()
    {
        var text = SearchText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var chat = Current;
        var message = new ChatMessage { Prompt = text, Files = [.. _pending] };
        _pending.Clear();
        chat.Messages.Add(message);
        SearchText = string.Empty;
        _context.Store.Save();
        RaiseItemsChanged();
        Respond(chat, message);
    }

    /// <summary>Streams the answer for a message (all earlier messages are the history).</summary>
    private void Respond(Chat chat, ChatMessage message)
    {
        var model = ModelOf(chat);
        if (model is null)
        {
            message.Answer = "_No model – add an API in GPT4Free Preferences._";
            message.Finished = true;
            return;
        }

        Cancel();
        var cts = _cts = new CancellationTokenSource();
        var history = new List<AiMessage>();
        if (chat.SystemPrompt.Length > 0)
        {
            history.Add(new AiMessage("system", chat.SystemPrompt));
        }

        foreach (var m in chat.Messages.TakeWhile(m => m != message))
        {
            history.Add(new AiMessage("user", Attachments.WithTextFiles(m.Prompt, m.Files), m.Files));
            history.Add(new AiMessage("assistant", m.Answer));
        }

        history.Add(new AiMessage("user", Attachments.WithTextFiles(message.Prompt, message.Files), message.Files));
        message.Answer = string.Empty;
        message.Finished = false;
        IsLoading = true;
        _ = Task.Run(async () =>
        {
            var text = new StringBuilder();
            var watch = Stopwatch.StartNew();
            var shown = 0L;
            try
            {
                await foreach (var piece in AiClient.Stream(model, history, chat.Creativity, cts.Token))
                {
                    text.Append(piece);
                    if (watch.ElapsedMilliseconds - shown > 100)
                    {
                        shown = watch.ElapsedMilliseconds;
                        message.Answer = text + " ▍";
                        Refresh(message);
                    }
                }

                message.Answer = text.ToString();
            }
            catch (OperationCanceledException)
            {
                message.Answer = text + "\n\n_Stopped._";
            }
            catch (Exception ex)
            {
                Log.Error("g4f chat", ex);
                message.Answer = $"{text}\n\n**Could not get a response:** {ex.Message}";
            }
            finally
            {
                message.Finished = true;
                if (_cts == cts)
                {
                    _cts = null;
                    IsLoading = false;
                }

                _context.Store.Save();
                Refresh(message);
            }

            if (chat.Messages.Count == 1 && chat.Name == "New Chat")
            {
                await NameChat(chat, model);
            }
        });
    }

    private void Refresh(ChatMessage message)
    {
        if (_items.TryGetValue(message.Id, out var entry))
        {
            Fill(entry.Item, entry.Details, message);
        }
    }

    private async Task NameChat(Chat chat, ModelRef model)
    {
        var message = chat.Messages[0];
        chat.Name = Shorten(message.Prompt, 40);
        if (_context.Settings.SmartChatNaming)
        {
            try
            {
                var name = await AiClient.Complete(model, [new AiMessage("user", $"Give this conversation a short title of at most 5 words. ONLY return the title, without quotes.\n\nUser: {Shorten(message.Prompt, 500)}\n\nAssistant: {Shorten(message.Answer, 500)}")], 0.3);
                if (name.Trim().Trim('"') is { Length: > 0 and <= 60 } title)
                {
                    chat.Name = title;
                }
            }
            catch (Exception ex)
            {
                Log.Error("g4f chat name", ex);
            }
        }

        _context.Store.Save();
        RaiseItemsChanged();
    }

    private void Cancel() => _cts?.Cancel();

    private IContextItem[] MessageActions(Chat chat, ChatMessage m)
    {
        var last = chat.Messages.LastOrDefault() == m;
        var actions = new List<IContextItem>
        {
            Ui.Action("Paste Response", "", () => G4fContext.Paste(m.Answer), Ui.CtrlShift(VirtualKey.V), CommandResult.Dismiss()),
            Ui.Copy(m.Prompt, "Copy Prompt", Ui.CtrlShift(VirtualKey.C)),
        };
        if (!m.Finished)
        {
            actions.Add(Ui.Action("Stop Response", "", Cancel, Ui.Ctrl((VirtualKey)0xBE)));
        }

        if (last)
        {
            actions.Add(Ui.Action("Resend Message", "", () => Respond(chat, m), Ui.CtrlShift(VirtualKey.R)));
        }

        var edit = new FormBuilder().Input("prompt", "Message", m.Prompt, multiline: true, required: true).Text("Sends the message again; later messages of this chat are removed.", subtle: true).Build("Send");
        actions.Add(new CommandContextItem(new FormPage("Edit Message", edit, inputs =>
        {
            var index = chat.Messages.IndexOf(m);
            if (index >= 0)
            {
                chat.Messages.RemoveRange(index + 1, chat.Messages.Count - index - 1);
                m.Prompt = inputs.Str("prompt");
                _context.Store.Save();
                RaiseItemsChanged();
                Respond(chat, m);
            }

            return CommandResult.GoBack();
        }, Ui.Glyph(""))) { Title = "Edit Message", RequestedShortcut = Ui.Ctrl(VirtualKey.E) });
        actions.Add(Ui.Confirm("Delete Message", "", "Delete this message?", Shorten(m.Prompt, 120), () =>
        {
            chat.Messages.Remove(m);
            _items.Remove(m.Id);
            _context.Store.Save();
            RaiseItemsChanged();
        }, critical: true));
        actions.AddRange(ChatActions());
        return [.. actions];
    }

    private IContextItem[] ChatActions()
    {
        var chat = Current;
        return
        [
            new CommandContextItem(SettingsForm(null)) { Title = "New Chat", RequestedShortcut = Ui.Ctrl(VirtualKey.N) },
            Ui.Action("Attach File…", "", AttachFile, Ui.CtrlShift(VirtualKey.A)),
            new CommandContextItem(SettingsForm(chat)) { Title = "Chat Settings", RequestedShortcut = Ui.CtrlShift(VirtualKey.S) },
            new CommandContextItem(new ModelPickerPage(_context, model =>
            {
                chat.Model = model.Key;
                _context.Store.Save();
                RaiseItemsChanged();
            })) { Title = "Change Model", RequestedShortcut = Ui.Ctrl(VirtualKey.M) },
            Ui.Action(chat.Pinned ? "Unpin Chat" : "Pin Chat", "", () =>
            {
                chat.Pinned = !chat.Pinned;
                _context.Store.Save();
                RaiseItemsChanged();
            }, Ui.CtrlShift(VirtualKey.P)),
            new CommandContextItem(new SearchChatsPage(_context, () => RaiseItemsChanged())) { Title = "Search in Chats", RequestedShortcut = Ui.CtrlShift(VirtualKey.F) },
            Ui.Action("Duplicate Chat", "", () =>
            {
                var copy = JsonSerializer.Deserialize<Chat>(JsonSerializer.Serialize(chat))!;
                copy.Id = Guid.NewGuid().ToString("N")[..10];
                copy.Name = $"{chat.Name} (copy)";
                copy.Created = DateTime.Now;
                foreach (var message in copy.Messages)
                {
                    message.Id = DateTime.UtcNow.Ticks + Random.Shared.Next(1, 1_000_000);
                }

                State.Chats.Insert(0, copy);
                State.CurrentChat = copy.Id;
                _context.Store.Save();
                RaiseItemsChanged();
            }),
            Ui.Action("Export Chat", "", () => Export(chat)),
            Ui.Action("Import Chat…", "", Import),
            Ui.Confirm("Delete Chat", "", $"Delete \"{chat.Name}\"?", "The chat and its messages are removed.", () =>
            {
                Cancel();
                State.Chats.Remove(chat);
                State.CurrentChat = State.Chats.FirstOrDefault()?.Id ?? string.Empty;
                _context.Store.Save();
                RaiseItemsChanged();
            }, critical: true),
            Ui.Confirm("Delete All Chats", "", "Delete all chats?", "Every chat and message of AI Chat is removed.", () =>
            {
                Cancel();
                State.Chats.Clear();
                State.CurrentChat = string.Empty;
                _items.Clear();
                _context.Store.Save();
                RaiseItemsChanged();
            }, critical: true),
        ];
    }

    private void AttachFile() => _ = Task.Run(async () =>
    {
        if (await FileDialog.Open("Attach a file to the next message", Attachments.Filter) is { } file)
        {
            _pending.Add(file);
            RaiseItemsChanged();
        }
    });

    private void Export(Chat chat)
    {
        var markdown = new StringBuilder($"# {chat.Name}\n\n");
        foreach (var m in chat.Messages)
        {
            markdown.Append(CultureInfo.InvariantCulture, $"**You:** {m.Prompt}\n\n{m.Answer}\n\n---\n\n");
        }

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var safe = string.Concat(chat.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var file = Path.Combine(downloads, $"{safe}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(chat, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        ClipboardHelper.SetText(markdown.ToString());
        Status.Info($"Copied as markdown, saved as {file}");
    }

    /// <summary>Imports a chat exported here, or one from the Raycast extension (first/second messages).</summary>
    private void Import() => _ = Task.Run(async () =>
    {
        if (await FileDialog.Open("Import a chat", "Chat (JSON)\0*.json\0All files\0*.*\0") is not { } file)
        {
            return;
        }

        try
        {
            var json = JsonNode.Parse(await File.ReadAllTextAsync(file)) ?? throw new InvalidDataException("empty file");
            var node = json is JsonArray array ? array[0]! : json;
            var chat = new Chat { Name = Graph.Str(node["name"]) is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(file), SystemPrompt = Graph.Str(node["systemPrompt"]) };
            foreach (var m in (node["messages"] as JsonArray ?? []).OfType<JsonNode>())
            {
                var prompt = Graph.Str(m["prompt"]) is { Length: > 0 } p ? p : Graph.Str(m["first"]?["content"]);
                var answer = Graph.Str(m["answer"]) is { Length: > 0 } a ? a : Graph.Str(m["second"]?["content"]);
                if (prompt.Length > 0 && Graph.Str(m["first"]?["role"]) != "system")
                {
                    chat.Messages.Add(new ChatMessage { Id = DateTime.UtcNow.Ticks + chat.Messages.Count, Prompt = prompt, Answer = answer, Finished = true });
                }
            }

            State.Chats.Insert(0, chat);
            State.CurrentChat = chat.Id;
            _context.Store.Save();
            Status.Info($"Imported \"{chat.Name}\" ({chat.Messages.Count} messages)");
            RaiseItemsChanged();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or InvalidOperationException)
        {
            Status.Error($"Import failed: {ex.Message}");
        }
    });

    /// <summary>New chat (chat = null) or the settings of a chat.</summary>
    private FormPage SettingsForm(Chat? chat)
    {
        var presets = State.Presets;
        var form = new FormBuilder()
            .Input("name", "Chat name", chat?.Name ?? string.Empty, chat is null ? "New Chat" : string.Empty);
        if (presets.Count > 0)
        {
            form.Choice("preset", "AI preset (overrides the options below)", [("None", string.Empty), .. presets.Select(p => (p.Name, p.Name))], chat is null ? presets.FirstOrDefault(p => p.IsDefault)?.Name ?? string.Empty : string.Empty);
        }

        form.Choice("model", "Model", _context.ModelChoices(withDefault: true), chat?.Model ?? string.Empty)
            .Choice("creativity", "Creativity", CreativityChoices, (chat?.Creativity ?? 0.7).ToString(CultureInfo.InvariantCulture))
            .Input("systemPrompt", "System prompt", chat?.SystemPrompt ?? string.Empty, "Optional instructions for the AI", multiline: true);
        return new FormPage(chat is null ? "New Chat" : "Chat Settings", form.Build(chat is null ? "Create Chat" : "Save"), inputs =>
        {
            var preset = presets.FirstOrDefault(p => p.Name == inputs.Str("preset"));
            var target = chat ?? NewChat();
            target.Name = inputs.Str("name") is { Length: > 0 } name ? name : target.Name;
            target.Model = preset?.Model ?? inputs.Str("model");
            target.Creativity = preset?.Creativity ?? (double.TryParse(inputs.Str("creativity"), NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c : 0.7);
            target.SystemPrompt = preset?.SystemPrompt ?? inputs["systemPrompt"]?.GetValue<string>() ?? string.Empty;
            _context.Store.Save();
            RaiseItemsChanged();
            return CommandResult.GoBack();
        }, Ui.Glyph(""));
    }
}

/// <summary>Full-text search over the messages of all chats.</summary>
internal sealed partial class SearchChatsPage : DynamicListPage
{
    private readonly G4fContext _context;
    private readonly Action _switched;

    public SearchChatsPage(G4fContext context, Action switched)
    {
        _context = context;
        _switched = switched;
        Name = "Search";
        Title = "Search in Chats";
        Icon = Ui.Glyph("");
        PlaceholderText = "Search messages of all chats …";
        ShowDetails = true;
    }

    public override void UpdateSearchText(string oldSearch, string newSearch) => RaiseItemsChanged();

    // searches prompts and answers (Command Palette's own filter only sees title and subtitle)
    public override IListItem[] GetItems() =>
        [.. _context.Store.State.Chats.SelectMany(chat => chat.Messages.AsEnumerable().Reverse()
            .Where(m => SearchText.Trim().Length == 0 || m.Prompt.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase) || m.Answer.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase))
            .Select(m => (IListItem)new ListItem(new AnonymousCommand(() =>
        {
            _context.Store.State.CurrentChat = chat.Id;
            _context.Store.Save();
            _switched();
        }) { Name = "Open Chat", Result = CommandResult.GoBack() })
        {
            Title = m.Prompt.Length > 120 ? m.Prompt[..120] + "…" : m.Prompt,
            Subtitle = chat.Name,
            Section = chat.Name,
            Icon = Ui.Glyph(""),
            Details = new Details { Title = chat.Name, Body = $"**You:** {m.Prompt}\n\n---\n\n{m.Answer}", Size = ContentSize.Large },
            MoreCommands = [Ui.Copy(m.Answer, "Copy Response", Ui.CtrlShift(VirtualKey.C))],
        }))];
}
