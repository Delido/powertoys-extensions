using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.G4f;

[Guid("E8DC0AC9-D09B-463E-8081-EEB3792529D0")]
public sealed partial class G4fExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new G4fProvider();
}

public sealed partial class G4fProvider : CommandProvider
{
    public const string ProviderId = "delido.g4f";
    private readonly ICommandItem[] _commands;

    public G4fProvider()
    {
        var context = G4fContext.Shared = new G4fContext();
        Id = ProviderId;
        DisplayName = "GPT4Free";
        Icon = Ui.Glyph("");
        Settings = context.Settings.Settings;
        var asks = Prompts.Commands.Select(spec => (ICommandItem)new CommandItem(new AskPage(context, spec)) { Title = spec.Title, Subtitle = spec.Subtitle }).ToList();
        asks.Insert(1, new CommandItem(context.Chat) { Title = "AI Chat", Subtitle = "GPT4Free · chat with your AI models, history is kept" });
        _commands =
        [
            .. asks,
            new CommandItem(new ImagePage(context)) { Title = "Generate Images", Subtitle = "GPT4Free · images from a text prompt" },
            new CommandItem(new CustomCommandsPage(context)) { Title = "Custom AI Commands", Subtitle = "GPT4Free · your own prompts with placeholders" },
            new CommandItem(new PreferencesPage(context)) { Title = "GPT4Free Preferences", Subtitle = "APIs, default model, AI presets" },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem? GetCommandItem(string id) => _commands.FirstOrDefault(c => c.Command?.Id == id);
}

/// <summary>Type a prompt in the search box, Enter generates; earlier images stay in the list.</summary>
internal sealed partial class ImagePage : DynamicListPage
{
    private static readonly string[] Sizes = ["1024x1024", "1536x1024", "1024x1536", "1792x1024", "1024x1792", "512x512"];
    private readonly G4fContext _context;
    private readonly ListItem _generate;
    private string? _working;

    public ImagePage(G4fContext context)
    {
        _context = context;
        Id = "g4f.generate-images";
        Name = "Open";
        Title = "Generate Images";
        Icon = Ui.Glyph("");
        PlaceholderText = "Describe the image … (Enter generates)";
        ShowDetails = true;
        _generate = new ListItem(new AnonymousCommand(Generate) { Name = "Generate", Result = CommandResult.KeepOpen() }) { Icon = Ui.Glyph("") };
    }

    private G4fState State => _context.Store.State;

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        if (string.IsNullOrWhiteSpace(oldSearch) != string.IsNullOrWhiteSpace(newSearch))
        {
            RaiseItemsChanged();
        }
        else
        {
            _generate.Title = $"Generate: {newSearch.Trim()}";
        }
    }

    public override IListItem[] GetItems()
    {
        var model = _context.Store.Find(State.ImageModel);
        var items = new List<IListItem>();
        var settings = new CommandContextItem(SettingsPage()) { Title = "Image Model and Size", RequestedShortcut = Ui.CtrlShift(VirtualKey.S) };
        if (model is null)
        {
            items.Add(new ListItem(SettingsPage()) { Title = "Choose the image model first", Subtitle = "e.g. dall-e-3 or gpt-image-1 of an OpenAI-compatible API", Icon = Ui.Glyph("") });
        }
        else if (!string.IsNullOrWhiteSpace(SearchText))
        {
            _generate.Title = $"Generate: {SearchText.Trim()}";
            _generate.Subtitle = $"{model.Label} · {State.ImageSize}";
            _generate.MoreCommands = [settings];
            items.Add(_generate);
        }

        if (_working is { } prompt)
        {
            items.Add(new ListItem(new NoOpCommand()) { Title = $"Generating: {prompt}", Subtitle = "This can take a minute", Icon = Ui.Glyph("") });
        }

        foreach (var image in State.Images.AsEnumerable().Reverse())
        {
            var uri = new Uri(image.File).AbsoluteUri;
            items.Add(new ListItem(new AnonymousCommand(() => Proc.Open(image.File)) { Name = "Open Image", Result = CommandResult.Dismiss() })
            {
                Title = image.Prompt,
                Subtitle = image.Model,
                Icon = Ui.Glyph(""),
                Tags = [Ui.Tag(Ui.Ago(image.Created))],
                Details = new Details { Title = image.Prompt, Body = File.Exists(image.File) ? $"![]({uri}?--x-cmdpal-fit=fit)" : "_The file was deleted._", Size = ContentSize.Large },
                MoreCommands =
                [
                    Ui.Copy(image.File, "Copy File Path", Ui.CtrlShift(VirtualKey.C)),
                    Ui.Copy(image.Prompt, "Copy Prompt", Ui.CtrlShift((VirtualKey)0xBE)),
                    Ui.Action("Show in Folder", "", () => Proc.Start("explorer.exe", $"/select,{image.File}")),
                    settings,
                    Ui.Confirm("Delete Image", "", "Delete this image?", image.Prompt, () =>
                    {
                        State.Images.Remove(image);
                        File.Delete(image.File);
                        _context.Store.Save();
                        RaiseItemsChanged();
                    }, critical: true),
                ],
            });
        }

        return [.. items];
    }

    private void Generate()
    {
        var prompt = SearchText.Trim();
        if (prompt.Length == 0 || _context.Store.Find(State.ImageModel) is not { } model)
        {
            return;
        }

        _working = prompt;
        SearchText = string.Empty;
        IsLoading = true;
        RaiseItemsChanged();
        _ = Task.Run(async () =>
        {
            try
            {
                var png = await AiClient.GenerateImage(model, prompt, State.ImageSize);
                var file = Path.Combine(_context.Store.ImagesDir, $"{DateTime.Now:yyyyMMdd-HHmmss}.png");
                await File.WriteAllBytesAsync(file, png);
                State.Images.Add(new ImageEntry { Prompt = prompt, File = file, Model = model.Label });
                _context.Store.Save();
            }
            catch (Exception ex)
            {
                Log.Error("g4f image", ex);
                Status.Error($"Image failed: {ex.Message}");
            }
            finally
            {
                _working = null;
                IsLoading = false;
                RaiseItemsChanged();
            }
        });
    }

    private FormPage SettingsPage()
    {
        var form = new FormBuilder()
            .Text("Uses the OpenAI images API (/images/generations) of the chosen API, e.g. OpenAI dall-e-3 / gpt-image-1 or a compatible gateway.", subtle: true)
            .Choice("model", "Image model", _context.ModelChoices(), State.ImageModel)
            .Choice("size", "Size", Sizes.Select(s => (s, s)), State.ImageSize)
            .Build("Save");
        return new FormPage("Image Model and Size", form, inputs =>
        {
            State.ImageModel = inputs.Str("model");
            State.ImageSize = inputs.Str("size") is { Length: > 0 } size ? size : "1024x1024";
            _context.Store.Save();
            RaiseItemsChanged();
            return CommandResult.GoBack();
        }, Ui.Glyph(""));
    }
}

internal sealed partial class CustomCommandsPage : ListPage
{
    private const string PlaceholderHelp = "Placeholders: {input} (the text you enter), {clipboard}, {date}, {time}, {datetime}, {day}, {shell|command}, {echo|text}; modifiers: {input|uppercase|lowercase|trim|percent-encode|json-stringify|escape}.";
    private readonly G4fContext _context;
    private readonly Dictionary<string, AskPage> _pages = [];

    public CustomCommandsPage(G4fContext context)
    {
        _context = context;
        Id = "g4f.custom-ai-commands";
        Name = "Open";
        Title = "Custom AI Commands";
        Icon = Ui.Glyph("");
        PlaceholderText = "Filter commands …";
    }

    public override IListItem[] GetItems()
    {
        var commands = _context.Store.State.Commands;
        var items = commands.Select(c => (IListItem)new ListItem(_pages.TryGetValue(c.Id, out var page) ? page : _pages[c.Id] = new AskPage(_context, Prompts.ForCustom(c)))
        {
            Title = c.Name,
            Subtitle = c.Prompt.Replace('\n', ' '),
            Icon = Ui.Glyph(""),
            MoreCommands =
            [
                new CommandContextItem(Editor(c)) { Title = "Edit Command", RequestedShortcut = Ui.Ctrl(VirtualKey.E) },
                new CommandContextItem(Editor(null)) { Title = "Create Command", RequestedShortcut = Ui.Ctrl(VirtualKey.N) },
                Ui.Confirm("Delete Command", "", $"Delete \"{c.Name}\"?", c.Prompt, () =>
                {
                    commands.Remove(c);
                    _pages.Remove(c.Id);
                    _context.Store.Save();
                    RaiseItemsChanged();
                }, critical: true),
            ],
        }).ToList();
        items.Add(new ListItem(Editor(null)) { Title = "Create Custom Command", Subtitle = "Your own prompt, e.g. \"Write a commit message for: {input}\"", Icon = Ui.Glyph("") });
        return [.. items];
    }

    private FormPage Editor(CustomCommand? command)
    {
        var form = new FormBuilder()
            .Input("name", "Name", command?.Name ?? string.Empty, required: true)
            .Input("prompt", "Prompt", command?.Prompt ?? string.Empty, "Summarize in three bullet points: {input}", multiline: true, required: true)
            .Text(PlaceholderHelp, subtle: true)
            .Choice("model", "Model", _context.ModelChoices(withDefault: true), command?.Model ?? string.Empty)
            .Build("Save");
        return new FormPage(command is null ? "Create Custom Command" : "Edit Command", form, inputs =>
        {
            var target = command ?? new CustomCommand();
            target.Name = inputs.Str("name");
            target.Prompt = inputs["prompt"]?.GetValue<string>() ?? string.Empty;
            target.Model = inputs.Str("model");
            if (command is null)
            {
                _context.Store.State.Commands.Add(target);
            }

            if (target.Model.Length > 0)
            {
                _context.Store.State.CommandModels[$"custom-{target.Id}"] = target.Model;
            }
            else
            {
                _context.Store.State.CommandModels.Remove($"custom-{target.Id}");
            }

            _pages.Remove(target.Id);
            _context.Store.Save();
            RaiseItemsChanged();
            return CommandResult.GoBack();
        }, Ui.Glyph(""));
    }
}

internal sealed partial class PreferencesPage : ListPage
{
    private readonly G4fContext _context;

    public PreferencesPage(G4fContext context)
    {
        _context = context;
        Id = "g4f.preferences";
        Name = "Open";
        Title = "GPT4Free Preferences";
        Icon = Ui.Glyph("");
    }

    public override IListItem[] GetItems()
    {
        var state = _context.Store.State;
        return
        [
            new ListItem(new ApisPage(_context)) { Title = "Custom APIs", Subtitle = $"{state.Apis.Count} API(s) · OpenAI, Anthropic, Gemini, OpenRouter, Ollama …", Icon = Ui.Glyph("") },
            new ListItem(new ModelsPage(_context)) { Title = "Default Model", Subtitle = _context.Store.ModelFor()?.Label ?? "No model yet", Icon = Ui.Glyph("") },
            new ListItem(new PresetsPage(_context)) { Title = "AI Presets", Subtitle = $"{state.Presets.Count} preset(s) for AI Chat · model, creativity, system prompt", Icon = Ui.Glyph("") },
            new ListItem(Ui.OpenSettings(G4fProvider.ProviderId)) { Title = "Settings", Subtitle = $"Default language: {_context.Settings.DefaultLanguage} · smart chat naming · translate", Icon = Ui.Glyph("") },
        ];
    }
}

internal sealed partial class ApisPage : ListPage
{
    private readonly G4fContext _context;

    public ApisPage(G4fContext context)
    {
        _context = context;
        Name = "Open";
        Title = "Custom APIs";
        Icon = Ui.Glyph("");
    }

    public override IListItem[] GetItems()
    {
        var apis = _context.Store.State.Apis;
        var items = apis.Select(api => (IListItem)new ListItem(Editor(api))
        {
            Title = api.Label,
            Subtitle = $"{api.Url} · {api.Models.Count} model(s)",
            Icon = Ui.Glyph(""),
            Tags = [Ui.Tag(api.Type), api.KeyProtected.Length > 0 ? Ui.Green("key") : Ui.Gray("no key")],
            MoreCommands =
            [
                Ui.Action("Refresh Models", "", () => Refresh(api), Ui.Ctrl(VirtualKey.R)),
                Ui.Confirm("Delete API", "", $"Delete \"{api.Label}\"?", "Its models disappear from all commands.", () =>
                {
                    apis.Remove(api);
                    _context.Store.Save();
                    RaiseItemsChanged();
                }, critical: true),
            ],
        }).ToList();
        items.Add(new ListItem(Editor(null)) { Title = "Add API", Subtitle = "OpenAI, Anthropic, Google Gemini, OpenRouter, Groq, Mistral, DeepSeek, Ollama, LM Studio or any compatible URL", Icon = Ui.Glyph("") });
        return [.. items];
    }

    private void Refresh(CustomApi api) => Status.Start($"Loading the models of {api.Label} …", $"{api.Label}: models updated", async () =>
    {
        var models = await AiClient.ListModels(api);
        if (models.Count == 0)
        {
            throw new InvalidOperationException("The API returned no models – enter them by hand (edit the API)");
        }

        api.Models = models;
        _context.Store.Save();
    }, () => RaiseItemsChanged());

    private FormPage Editor(CustomApi? api)
    {
        var form = new FormBuilder();
        if (api is null)
        {
            form.Choice("template", "Provider", [.. AiClient.Templates.Select(t => (t.Name, t.Name)), ("Other (enter the URL)", string.Empty)], "OpenAI");
        }

        form.Input("name", "Name", api?.Name ?? string.Empty, api is null ? "Optional, defaults to the provider" : string.Empty)
            .Input("url", "Base URL", api?.Url ?? string.Empty, api is null ? "Only for \"Other\", e.g. https://my-gateway/v1" : "https://…/v1")
            .Choice("type", "API type", [("OpenAI compatible (chat/completions)", "OpenAI"), ("Anthropic (messages)", "Anthropic")], api?.Type ?? "OpenAI")
            .Input("key", "API key", string.Empty, api?.KeyProtected.Length > 0 ? "Stored – leave empty to keep it" : "Not needed for local servers", password: true)
            .Input("models", "Models", string.Join(", ", api?.Models ?? []), "Comma-separated; empty = load from the API", multiline: true)
            .Text("The key is stored encrypted for your Windows account.", subtle: true);
        return new FormPage(api is null ? "Add API" : $"Edit {api.Label}", form.Build("Save"), inputs =>
        {
            var target = api ?? new CustomApi();
            var template = AiClient.Templates.FirstOrDefault(t => t.Name == inputs.Str("template"));
            target.Name = inputs.Str("name") is { Length: > 0 } name ? name : template.Name ?? target.Name;
            target.Url = inputs.Str("url") is { Length: > 0 } url ? url.TrimEnd('/') : template.Url ?? target.Url;
            target.Type = api is null && template.Name is not null ? template.Type : inputs.Str("type");
            if (target.Url.Length == 0)
            {
                return CommandResult.ShowToast(new ToastArgs { Message = "Enter the base URL", Result = CommandResult.KeepOpen() });
            }

            if (inputs.Str("key") is { Length: > 0 } key)
            {
                target.Key = key;
            }

            target.Models = [.. inputs.Str("models").Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()];
            if (api is null)
            {
                _context.Store.State.Apis.Add(target);
            }

            _context.Store.Save();
            if (target.Models.Count == 0)
            {
                Refresh(target);
            }

            RaiseItemsChanged();
            return CommandResult.GoBack();
        }, Ui.Glyph(""));
    }
}

internal sealed partial class ModelsPage : ListPage
{
    private readonly G4fContext _context;

    public ModelsPage(G4fContext context)
    {
        _context = context;
        Name = "Open";
        Title = "Default Model";
        Icon = Ui.Glyph("");
        PlaceholderText = "Filter models …";
    }

    public override IListItem[] GetItems()
    {
        var current = _context.Store.ModelFor()?.Key;
        var models = _context.Store.Models.ToArray();
        return models.Length == 0
            ? [G4fContext.NoModels()]
            : [.. models.Select(m => (IListItem)new ListItem(new AnonymousCommand(() =>
            {
                _context.Store.State.DefaultModel = m.Key;
                _context.Store.Save();
                RaiseItemsChanged();
            }) { Name = "Set as Default", Result = CommandResult.ShowToast($"Default model: {m.Label}") })
            {
                Title = m.Model,
                Subtitle = m.Api.Label,
                Section = m.Api.Label,
                Icon = Ui.Glyph(m.Key == current ? "" : ""),
                Tags = m.Key == current ? [Ui.Green("Default")] : [],
                MoreCommands =
                [
                    Ui.Action("Reset Models Chosen per Command", "", () =>
                    {
                        _context.Store.State.CommandModels.Clear();
                        _context.Store.Save();
                    }, result: CommandResult.ShowToast("All commands use the default model again")),
                ],
            })];
    }
}

internal sealed partial class PresetsPage : ListPage
{
    private readonly G4fContext _context;

    public PresetsPage(G4fContext context)
    {
        _context = context;
        Name = "Open";
        Title = "AI Presets";
        Icon = Ui.Glyph("");
    }

    public override IListItem[] GetItems()
    {
        var presets = _context.Store.State.Presets;
        var items = presets.Select(p => (IListItem)new ListItem(Editor(p))
        {
            Title = p.Name,
            Subtitle = $"{_context.Store.Find(p.Model)?.Label ?? "Default model"} · creativity {p.Creativity.ToString(CultureInfo.InvariantCulture)} · {p.SystemPrompt.Replace('\n', ' ')}",
            Icon = Ui.Glyph(""),
            Tags = p.IsDefault ? [Ui.Green("Default")] : [],
            MoreCommands =
            [
                Ui.Confirm("Delete Preset", "", $"Delete \"{p.Name}\"?", string.Empty, () =>
                {
                    presets.Remove(p);
                    _context.Store.Save();
                    RaiseItemsChanged();
                }, critical: true),
            ],
        }).ToList();
        items.Add(new ListItem(Editor(null)) { Title = "Create Preset", Subtitle = "Model, creativity and system prompt for new chats", Icon = Ui.Glyph("") });
        return [.. items];
    }

    private FormPage Editor(AiPreset? preset)
    {
        var form = new FormBuilder()
            .Input("name", "Name", preset?.Name ?? string.Empty, required: true)
            .Choice("model", "Model", _context.ModelChoices(withDefault: true), preset?.Model ?? string.Empty)
            .Choice("creativity", "Creativity", [("None (0.0)", "0"), ("Low (0.3)", "0.3"), ("Medium (0.7)", "0.7"), ("High (1.0)", "1"), ("Very high (1.5)", "1.5")], (preset?.Creativity ?? 0.7).ToString(CultureInfo.InvariantCulture))
            .Input("systemPrompt", "System prompt", preset?.SystemPrompt ?? string.Empty, multiline: true)
            .Toggle("isDefault", "Default for new chats", preset?.IsDefault ?? false)
            .Build("Save");
        return new FormPage(preset is null ? "Create Preset" : $"Edit {preset.Name}", form, inputs =>
        {
            var presets = _context.Store.State.Presets;
            var target = preset ?? new AiPreset();
            target.Name = inputs.Str("name");
            target.Model = inputs.Str("model");
            target.Creativity = double.TryParse(inputs.Str("creativity"), NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c : 0.7;
            target.SystemPrompt = inputs["systemPrompt"]?.GetValue<string>() ?? string.Empty;
            target.IsDefault = inputs.Bool("isDefault");
            if (target.IsDefault)
            {
                presets.ForEach(p => p.IsDefault = p == target);
            }

            if (preset is null)
            {
                presets.Add(target);
            }

            _context.Store.Save();
            RaiseItemsChanged();
            return CommandResult.GoBack();
        }, Ui.Glyph(""));
    }
}
