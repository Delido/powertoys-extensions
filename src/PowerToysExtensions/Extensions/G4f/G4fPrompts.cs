using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.G4f;

public enum AskKind
{
    Normal,
    Translate,
    Screen,
    WebPage,
    Custom,
}

/// <summary>
/// One "ask" command. Raycast reads the selected text; Command Palette cannot, so the text field starts
/// with the clipboard (copy the text first, or type it).
/// </summary>
public sealed record AskSpec(
    string Id,
    string Title,
    string Subtitle,
    string Glyph,
    string Context = "",
    string TextLabel = "Text",
    string? QueryLabel = null,
    bool FromClipboard = true,
    bool DefaultLanguage = false,
    AskKind Kind = AskKind.Normal,
    CustomCommand? Custom = null);

public static class Prompts
{
    public static readonly AskSpec[] Commands =
    [
        new("ask-ai", "Ask AI", "GPT4Free · ask anything", "", TextLabel: "Prompt", FromClipboard: false),
        new("ask-about-selected-text", "Ask About Selected Text", "GPT4Free · ask a question about copied text", "", QueryLabel: "Query"),
        new("ask-about-screen-content", "Ask About Screen Content", "GPT4Free · captures the screen and asks a vision model", "", TextLabel: "Query", FromClipboard: false, Kind: AskKind.Screen),
        new("ask-about-web-page", "Ask About Web Page", "GPT4Free · reads a web page (URL from the clipboard)", "", TextLabel: "URL", QueryLabel: "Query", Kind: AskKind.WebPage),
        new("continue", "Continue Text", "GPT4Free · generate a continuation", "", "Generate a natural continuation of the following text. Try to keep your continuation consistent with the original. ONLY return the continued text and nothing else.", "Text to continue"),
        new("summarize", "Summarize", "GPT4Free · summarize text", "", "Summarize the given content.", DefaultLanguage: true),
        new("explain", "Explain", "GPT4Free · explain text", "", "Explain the following text.", DefaultLanguage: true),
        new("fix-code", "Fix Code", "GPT4Free · fix errors and bugs in code", "", "Fix errors and bugs in the given code, without changing its intended functionality. ONLY return the fixed code and nothing else. Enclose the entire code in a single code block.", "Code"),
        new("format", "Format Text", "GPT4Free · fix the formatting of text", "", "Fix the formatting of the following text. ONLY return the modified text and nothing else."),
        new("friendly", "Change Tone to Friendly", "GPT4Free · make text friendlier", "", "Make the following text seem more friendly. Use the same language as the original text. ONLY return the modified text and nothing else."),
        new("professional", "Change Tone to Professional", "GPT4Free · make text more professional", "", "In the following text, change it to use advanced vocabulary but do not overuse it. Make sure to use proper grammar and spell check thoroughly. Show expertise in the subject provided, but do not add any extra information. Try to keep your response at the same length of words as the original. Use the same language as the original text. ONLY return the modified text and nothing else."),
        new("paraphrase", "Paraphrase", "GPT4Free · rephrase text", "", "Paraphrase the following text. Make sure to keep the tone and meaning of the text the same, and write in a natural style as far as possible. Try to keep your response at the same length of words as the original. ONLY return the paraphrased text and nothing else."),
        new("synonyms", "Find Synonyms", "GPT4Free · synonyms for a word or phrase", "", "Find a list of at most 15 synonyms for the given word/phrase, separated in bullet points, ordered by relevance. Only return the synonyms, and add nothing else. Keep the casing of the word/phrase the same. Your response should only return the BULLET POINTS of the synonyms.", "Word/phrase"),
        new("translate", "Translate", "GPT4Free · translate text", "", TextLabel: "Text to translate", Kind: AskKind.Translate),
        new("improve", "Improve Writing", "GPT4Free · correct and improve text", "", """
            Act as a spelling corrector, content editor, and text improver. ONLY return the rewritten text and nothing else.

            Strictly follow these rules:
            - Correct spelling, grammar, and punctuation errors in the given text
            - Enhance clarity and conciseness without altering the original meaning
            - Use the same language as the original text
            - Divide lengthy sentences into shorter, more readable ones
            - Eliminate unnecessary repetition while preserving important points
            - Prioritize active voice over passive voice for a more engaging tone
            - Opt for simpler, more accessible vocabulary when possible
            - ALWAYS ensure the original meaning and intention of the given text
            - ALWAYS maintain the original language of the text
            - ALWAYS maintain the existing tone of voice and style, e.g. formal, casual, polite, etc.
            - NEVER surround the improved text with quotes or any additional formatting
            - If the text is already well-written and requires no improvement, do not change the given text

            Text to improve:
            """),
        new("longer", "Make Longer", "GPT4Free · expand text", "", "Make the following text longer without providing any extra information than what's given. Use the same language as the original text. ONLY return the elongated text and nothing else."),
        new("shorter", "Make Shorter", "GPT4Free · shorten text", "", "Make the following text shorter while keeping the core idea. Use the same language as the original text. ONLY return the shortened text and nothing else."),
    ];

    public static AskSpec ForCustom(CustomCommand command) =>
        new($"custom-{command.Id}", command.Name, "Custom AI command", "", TextLabel: "Input", Kind: AskKind.Custom, Custom: command);

    /// <summary>The text sent to the AI for the form values.</summary>
    public static async Task<string> Build(AskSpec spec, G4fSettings settings, string text, string query, string language)
    {
        var prompt = spec.Kind switch
        {
            AskKind.Translate => $"Translate the following text to {language}. ONLY return the translated text and nothing else.\n\n{text}",
            AskKind.WebPage => $"Web page content ({text}):\n{await WebPage(text)}\n\n{query}",
            AskKind.Custom => await Placeholders.Process(spec.Custom!.Prompt, text),
            _ => string.Join("\n\n", new[] { spec.Context, text, query ?? string.Empty }.Where(s => s.Length > 0)),
        };
        if (spec.DefaultLanguage && settings.DefaultLanguage != "English")
        {
            prompt = $"The default language is {settings.DefaultLanguage}. Respond in this language.\n\n{prompt}";
        }

        return prompt;
    }

    private static async Task<string> WebPage(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Enter the address of a web page (https://…)");
        }

        using var response = await Http.Client().GetAsync(uri);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        var markdown = Markdown.FromHtml(System.Text.RegularExpressions.Regex.Replace(html, @"<(script|style|noscript|svg)[\s\S]*?</\1>", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        return markdown.Length > 60_000 ? markdown[..60_000] + "\n[… truncated]" : markdown;
    }
}

/// <summary>
/// Placeholders of custom AI commands: {input}/{selection}, {clipboard}, {date}, {time}, {datetime}, {day},
/// {shell|command}, {echo|text}; with modifiers like {input|uppercase|trim}. Placeholders can be nested.
/// </summary>
public static class Placeholders
{
    private static readonly string[] Modifiers = ["uppercase", "lowercase", "trim", "percent-encode", "json-stringify", "escape"];

    public static async Task<string> Process(string prompt, string input)
    {
        var result = new StringBuilder();
        var i = 0;
        while (i < prompt.Length)
        {
            var end = prompt[i] == '{' ? Closing(prompt, i) : -1;
            if (end > 0)
            {
                var inner = await Process(prompt[(i + 1)..end], input);
                result.Append(await Placeholder(inner, input) ?? $"{{{inner}}}");
                i = end + 1;
            }
            else
            {
                result.Append(prompt[i++]);
            }
        }

        return result.ToString();
    }

    private static int Closing(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            depth += text[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The value of a placeholder, or null when it is not one (then it stays as written).</summary>
    private static async Task<string?> Placeholder(string text, string input)
    {
        var parts = text.Split('|');
        var main = parts[0].Trim();
        var now = DateTime.Now;
        string value;
        switch (main)
        {
            case "input":
            case "selection":
                value = input;
                break;
            case "clipboard":
                value = ClipboardHelper.GetText() ?? string.Empty;
                break;
            case "date":
                value = now.ToString("D", CultureInfo.CurrentCulture);
                break;
            case "time":
                value = now.ToString("t", CultureInfo.CurrentCulture);
                break;
            case "datetime":
                value = $"{now.ToString("D", CultureInfo.CurrentCulture)} at {now.ToString("t", CultureInfo.CurrentCulture)}";
                break;
            case "day":
                value = now.ToString("dddd", CultureInfo.CurrentCulture);
                break;
            case "shell":
                return await Shell(string.Join('|', parts.Skip(1)).Trim());
            case "echo":
                value = string.Join('|', parts.Skip(1).Where(p => !Modifiers.Contains(p.Trim()))).Trim();
                break;
            default:
                return null;
        }

        foreach (var modifier in parts.Skip(1).Select(p => p.Trim()))
        {
            value = modifier switch
            {
                "uppercase" => value.ToUpperInvariant(),
                "lowercase" => value.ToLowerInvariant(),
                "trim" => value.Trim(),
                "percent-encode" => Uri.EscapeDataString(value),
                "json-stringify" => JsonSerializer.Serialize(value),
                "escape" => JsonSerializer.Serialize(value)[1..^1],
                _ => value,
            };
        }

        return value;
    }

    private static async Task<string> Shell(string command)
    {
        var result = await Proc.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command], TimeSpan.FromSeconds(60));
        return (result.Output + result.Error).Trim();
    }
}
