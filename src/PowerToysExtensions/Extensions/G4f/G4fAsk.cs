using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.G4f;

public sealed class G4fContext
{
    private ChatPage? _chat;

    public G4fSettings Settings { get; } = new();

    public G4fStore Store { get; } = new();

    public ChatPage Chat => _chat ??= new ChatPage(this);

    public static IListItem NoModels() =>
        new ListItem(new ApisPage(Shared!)) { Title = "Add an AI API first", Subtitle = "Enter: add OpenAI, Anthropic, Gemini, OpenRouter, Ollama … with your API key", Icon = Ui.Glyph("") };

    public static G4fContext? Shared { get; set; }

    /// <summary>Copies the text, hides the palette and pastes it into the window that was in front before.</summary>
    public static void Paste(string text) => _ = Task.Run(async () =>
    {
        ClipboardHelper.SetText(text);
        await Task.Delay(350);
        Native.SendKeys(0x11, 0x56); // Ctrl+V
    });

    public (string Title, string Value)[] ModelChoices(bool withDefault = false) =>
        [.. (withDefault ? [("Default model", string.Empty)] : Array.Empty<(string, string)>()), .. Store.Models.Select(m => (m.Label, m.Key))];
}

/// <summary>Form with a callback (the page swaps it for the answer on submit).</summary>
internal sealed partial class CallbackFormContent(string template, Func<JsonObject, ICommandResult> submit) : FormContent
{
    public CallbackFormContent Init()
    {
        TemplateJson = template;
        return this;
    }

    public override ICommandResult SubmitForm(string inputs, string data)
    {
        try
        {
            return submit(JsonNode.Parse(inputs)?.AsObject() ?? []);
        }
        catch (Exception ex)
        {
            Log.Error("g4f form", ex);
            return CommandResult.ShowToast(new ToastArgs { Message = ex.Message, Result = CommandResult.KeepOpen() });
        }
    }
}

/// <summary>Ask AI and all text commands: form, then the streamed answer.</summary>
internal sealed partial class AskPage : ContentPage
{
    private readonly G4fContext _context;
    private readonly AskSpec _spec;
    private readonly MarkdownContent _answer = new();
    private CancellationTokenSource? _cts;
    private bool _showAnswer;
    private DateTime _finished = DateTime.MaxValue;
    private ModelRef? _override;
    private (string Prompt, List<string> Files)? _last;
    private string _response = string.Empty;

    public AskPage(G4fContext context, AskSpec spec)
    {
        _context = context;
        _spec = spec;
        Id = $"g4f.{spec.Id}";
        Name = "Open";
        Title = spec.Title;
        Icon = Ui.Glyph(spec.Glyph);
    }

    public override IContent[] GetContent()
    {
        // opened again a while after the last answer: start with a fresh form (and the current clipboard)
        if (_showAnswer && _cts is null && DateTime.Now - _finished > TimeSpan.FromSeconds(30))
        {
            ShowForm();
        }

        return _showAnswer ? [_answer] : [Form()];
    }

    private void ShowForm()
    {
        _showAnswer = false;
        _override = null;
        Commands = [];
        Title = _spec.Title;
    }

    private IContent Form()
    {
        var model = _context.Store.ModelFor(_spec.Id);
        if (model is null)
        {
            return new MarkdownContent { Body = "## No AI model yet\n\nGPT4Free works with your own AI APIs (OpenAI, Anthropic, Google Gemini, OpenRouter, Groq, Mistral, a local Ollama …).\n\nOpen **GPT4Free Preferences → Custom APIs** and add one." };
        }

        var clip = _spec.FromClipboard ? ClipboardHelper.GetText() ?? string.Empty : string.Empty;
        if (_spec.Kind == AskKind.WebPage && !clip.Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            clip = string.Empty;
        }

        var form = new FormBuilder();
        if (_spec.Kind == AskKind.Custom)
        {
            form.Text($"Prompt: {_spec.Custom!.Prompt}", subtle: true);
        }
        else if (_spec.FromClipboard && _spec.Kind != AskKind.WebPage)
        {
            form.Text("Starts with the clipboard text – copy the text first, or type it.", subtle: true);
        }

        if (_spec.Kind == AskKind.Screen)
        {
            form.Text("Command Palette hides itself, captures all screens and comes back with the answer. The model must support images.", subtle: true);
        }

        form.Input("text", _spec.TextLabel, clip.Trim(), multiline: _spec.Kind is not (AskKind.WebPage), required: _spec.Kind != AskKind.Custom);
        if (_spec.QueryLabel is { } queryLabel)
        {
            form.Input("query", queryLabel, required: true, multiline: true);
        }

        if (_spec.Kind == AskKind.Translate && !_context.Settings.TranslateUseDefaultLanguage)
        {
            form.Choice("language", "Language", G4fSettings.Languages.Select(l => (l, l)), _context.Store.State.TranslateLanguage);
        }

        form.Choice("model", "Model", _context.ModelChoices(), model.Key);
        return new CallbackFormContent(form.Build("Ask"), Submit).Init();
    }

    private ICommandResult Submit(JsonObject inputs)
    {
        var state = _context.Store.State;
        var model = _context.Store.Find(inputs.Str("model"));
        if (model is not null && model.Key != _context.Store.ModelFor(_spec.Id)?.Key)
        {
            state.CommandModels[_spec.Id] = model.Key; // remember the choice per command, like Raycast
        }

        var language = _context.Settings.TranslateUseDefaultLanguage ? _context.Settings.DefaultLanguage : inputs.Str("language");
        if (_spec.Kind == AskKind.Translate && language.Length > 0)
        {
            state.TranslateLanguage = language;
        }

        _context.Store.Save();
        var text = inputs.Str("text");
        var query = inputs.Str("query");
        _showAnswer = true;
        _answer.Body = "_Thinking …_";
        _ = Task.Run(async () =>
        {
            try
            {
                var files = new List<string>();
                if (_spec.Kind == AskKind.Screen)
                {
                    files.Add(await CaptureScreen());
                }

                Generate(await Prompts.Build(_spec, _context.Settings, text, query, language), files);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        });
        RaiseItemsChanged();
        return CommandResult.KeepOpen();
    }

    private async Task<string> CaptureScreen()
    {
        _answer.Body = "_Capturing the screen …_";
        if (Palette.InForeground() is { } window)
        {
            Palette.Toggle(window);
        }

        await Task.Delay(450);
        var png = await ScreenCapture.Png();
        var file = Path.Combine(Paths.For("g4f"), "screenshot.png");
        await File.WriteAllBytesAsync(file, png);
        Palette.Summon();
        return file;
    }

    private void Generate(string prompt, List<string> files)
    {
        _last = (prompt, files);
        var model = _override ?? _context.Store.ModelFor(_spec.Id) ?? throw new InvalidOperationException("No model – add an API in the preferences");
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _response = string.Empty;
        _finished = DateTime.MaxValue;
        Title = model.Label;
        IsLoading = true;
        UpdateCommands();
        _ = Task.Run(async () =>
        {
            var text = new StringBuilder();
            var watch = Stopwatch.StartNew();
            var shown = 0L;
            try
            {
                await foreach (var piece in AiClient.Stream(model, [new AiMessage("user", Attachments.WithTextFiles(prompt, files), files)], 0.7, cts.Token))
                {
                    text.Append(piece);
                    if (watch.ElapsedMilliseconds - shown > 80)
                    {
                        shown = watch.ElapsedMilliseconds;
                        _answer.Body = text + " ▍";
                    }
                }

                _response = text.ToString();
                _answer.Body = _response.Length > 0 ? _response : "_The model returned no text._";
                Title = $"{model.Label} · {_response.Length} chars · {watch.Elapsed.TotalSeconds:0.0} s";
            }
            catch (OperationCanceledException)
            {
                _response = text.ToString();
                _answer.Body = _response + "\n\n_Stopped._";
            }
            catch (Exception ex)
            {
                _response = text.ToString();
                Fail(ex);
            }
            finally
            {
                if (_cts == cts)
                {
                    _cts = null;
                    IsLoading = false;
                    _finished = DateTime.Now;
                    UpdateCommands();
                }
            }
        });
    }

    private void Fail(Exception ex)
    {
        Log.Error($"g4f {_spec.Id}", ex);
        _answer.Body = $"{_response}\n\n## Could not get a response\n\n{ex.Message}\n\nTry again (Ctrl+Shift+R) or another model (Ctrl+M).";
        IsLoading = false;
        _finished = DateTime.Now;
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        var commands = new List<IContextItem>
        {
            Ui.Action("Copy Response", "", () => ClipboardHelper.SetText(_response), Ui.CtrlShift(VirtualKey.C)),
            Ui.Action("Paste Response", "", () => G4fContext.Paste(_response), Ui.CtrlShift(VirtualKey.V), CommandResult.Dismiss()),
        };
        if (_last is { } last)
        {
            var model = _override ?? _context.Store.ModelFor(_spec.Id);
            commands.Add(new CommandContextItem(new ChatPage(_context, () => (last.Prompt, _response, last.Files, model))) { Title = "Continue in Chat", RequestedShortcut = Ui.Ctrl(VirtualKey.J) });
            commands.Add(Ui.Action("Resend Message", "", () => Generate(last.Prompt, last.Files), Ui.CtrlShift(VirtualKey.R)));
        }

        if (_cts is not null)
        {
            commands.Add(Ui.Action("Stop Response", "", () => _cts?.Cancel(), Ui.Ctrl((VirtualKey)0xBE)));
        }

        commands.Add(new CommandContextItem(new ModelPickerPage(_context, model =>
        {
            _override = model;
            if (_last is { } again)
            {
                Generate(again.Prompt, again.Files);
            }
        })) { Title = "Change Model", RequestedShortcut = Ui.Ctrl(VirtualKey.M) });
        commands.Add(Ui.Action("New Question", "", () =>
        {
            _cts?.Cancel();
            ShowForm();
            RaiseItemsChanged();
        }, Ui.Ctrl(VirtualKey.N)));
        Commands = [.. commands];
    }
}

/// <summary>Pick a model; the callback runs and the page goes back.</summary>
internal sealed partial class ModelPickerPage : ListPage
{
    private readonly G4fContext _context;
    private readonly Action<ModelRef> _picked;

    public ModelPickerPage(G4fContext context, Action<ModelRef> picked)
    {
        _context = context;
        _picked = picked;
        Name = "Change Model";
        Title = "Change Model";
        Icon = Ui.Glyph("");
        PlaceholderText = "Filter models …";
    }

    public override IListItem[] GetItems()
    {
        var models = _context.Store.Models.ToArray();
        return models.Length == 0
            ? [G4fContext.NoModels()]
            : [.. models.Select(m => (IListItem)new ListItem(new AnonymousCommand(() => _picked(m)) { Name = "Use", Result = CommandResult.GoBack() })
            {
                Title = m.Model,
                Subtitle = m.Api.Label,
                Section = m.Api.Label,
                Icon = Ui.Glyph(""),
            })];
    }
}
