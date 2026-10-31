using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerToysExtensions.Core;

/// <summary>Builds Adaptive Card forms (Raycast's Form) for Command Palette.</summary>
public sealed class FormBuilder
{
    private readonly List<object> _body = [];

    public FormBuilder Text(string text, bool bold = false, bool subtle = false)
    {
        _body.Add(new Dictionary<string, object>
        {
            ["type"] = "TextBlock",
            ["text"] = text,
            ["wrap"] = true,
            ["weight"] = bold ? "Bolder" : "Default",
            ["isSubtle"] = subtle,
        });
        return this;
    }

    public FormBuilder Input(string id, string label, string value = "", string placeholder = "", bool multiline = false, bool required = false, bool password = false)
    {
        var input = new Dictionary<string, object>
        {
            ["type"] = "Input.Text",
            ["id"] = id,
            ["label"] = label,
            ["value"] = value,
            ["placeholder"] = placeholder,
            ["isMultiline"] = multiline,
            ["isRequired"] = required,
        };
        if (password)
        {
            input["style"] = "Password";
        }

        if (required)
        {
            input["errorMessage"] = $"{label} is required";
        }

        _body.Add(input);
        return this;
    }

    public FormBuilder Choice(string id, string label, IEnumerable<(string Title, string Value)> choices, string value = "", bool expanded = false)
    {
        _body.Add(new Dictionary<string, object>
        {
            ["type"] = "Input.ChoiceSet",
            ["id"] = id,
            ["label"] = label,
            ["value"] = value,
            ["style"] = expanded ? "expanded" : "compact",
            ["choices"] = choices.Select(c => new Dictionary<string, string> { ["title"] = c.Title, ["value"] = c.Value }).ToArray(),
        });
        return this;
    }

    public FormBuilder Toggle(string id, string title, bool value = false)
    {
        _body.Add(new Dictionary<string, object>
        {
            ["type"] = "Input.Toggle",
            ["id"] = id,
            ["title"] = title,
            ["value"] = value ? "true" : "false",
            ["valueOn"] = "true",
            ["valueOff"] = "false",
        });
        return this;
    }

    public FormBuilder Date(string id, string label, DateTime? value = null)
    {
        _body.Add(new Dictionary<string, object>
        {
            ["type"] = "Input.Date",
            ["id"] = id,
            ["label"] = label,
            ["value"] = value?.ToString("yyyy-MM-dd") ?? string.Empty,
        });
        return this;
    }

    public string Build(string submitTitle) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
        ["type"] = "AdaptiveCard",
        ["version"] = "1.5",
        ["body"] = _body,
        ["actions"] = new object[] { new Dictionary<string, object> { ["type"] = "Action.Submit", ["title"] = submitTitle } },
    });
}

/// <summary>A page with one form. onSubmit gets the inputs and returns what Command Palette should do next.</summary>
public sealed partial class FormPage : ContentPage
{
    private readonly CallbackForm _form;

    public FormPage(string title, string template, Func<JsonObject, ICommandResult> onSubmit, IconInfo? icon = null)
    {
        Name = title;
        Title = title;
        Icon = icon ?? new IconInfo("");
        _form = new CallbackForm(template, onSubmit);
    }

    public override IContent[] GetContent() => [_form];

    private sealed partial class CallbackForm : FormContent
    {
        private readonly Func<JsonObject, ICommandResult> _onSubmit;

        public CallbackForm(string template, Func<JsonObject, ICommandResult> onSubmit)
        {
            TemplateJson = template;
            _onSubmit = onSubmit;
        }

        public override ICommandResult SubmitForm(string inputs, string data)
        {
            try
            {
                return _onSubmit(JsonNode.Parse(inputs)?.AsObject() ?? []);
            }
            catch (Exception ex)
            {
                Log.Error("form submit", ex);
                return CommandResult.ShowToast(new ToastArgs { Message = ex.Message, Result = CommandResult.KeepOpen() });
            }
        }
    }
}

public static class FormValues
{
    public static string Str(this JsonObject inputs, string key) => inputs[key]?.GetValue<string>()?.Trim() ?? string.Empty;

    public static bool Bool(this JsonObject inputs, string key) => inputs.Str(key) == "true";
}
