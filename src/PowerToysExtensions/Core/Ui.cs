using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.System;

namespace PowerToysExtensions.Core;

/// <summary>Helpers for the recurring pieces of the UI: icons, shortcuts, tags, standard actions.</summary>
public static class Ui
{
    /// <summary>Segoe Fluent Icons glyph.</summary>
    public static IconInfo Glyph(string glyph) => new(glyph);

    public static IconInfo AppIcon(string file) => IconHelpers.FromRelativePath(Path.Combine("Assets", file));

    public static KeyChord Ctrl(VirtualKey key) => KeyChordHelpers.FromModifiers(ctrl: true, vkey: key);

    public static KeyChord CtrlShift(VirtualKey key) => KeyChordHelpers.FromModifiers(ctrl: true, shift: true, vkey: key);

    public static Tag Tag(string text, byte r, byte g, byte b) => new(text)
    {
        Foreground = ColorHelpers.FromRgb(r, g, b),
    };

    public static Tag Tag(string text) => new(text);

    // A few recurring colors (Raycast palette)
    public static Tag Red(string text) => Tag(text, 0xE5, 0x48, 0x4D);

    public static Tag Orange(string text) => Tag(text, 0xF7, 0x8C, 0x2B);

    public static Tag Yellow(string text) => Tag(text, 0xD9, 0xA4, 0x0F);

    public static Tag Green(string text) => Tag(text, 0x3B, 0xB2, 0x5B);

    public static Tag Blue(string text) => Tag(text, 0x3A, 0x8D, 0xE6);

    public static Tag Purple(string text) => Tag(text, 0x9B, 0x6B, 0xDF);

    public static Tag Gray(string text) => Tag(text, 0x8E, 0x8E, 0x93);

    public static CommandContextItem OpenUrl(string url, string title = "Open in Browser", KeyChord? shortcut = null) =>
        new(new OpenUrlCommand(url) { Name = title, Icon = Glyph("") }) { Title = title, RequestedShortcut = shortcut ?? default };

    public static CommandContextItem Copy(string text, string title, KeyChord? shortcut = null) =>
        new(new CopyTextCommand(text) { Name = title, Icon = Glyph("") }) { Title = title, RequestedShortcut = shortcut ?? default };

    /// <summary>Copy URL (Ctrl+Shift+C) and copy a markdown link (Ctrl+Shift+.).</summary>
    public static CommandContextItem[] CopyLinks(string url, string name) =>
    [
        Copy(url, "Copy URL", CtrlShift(VirtualKey.C)),
        Copy($"[{name}]({url})", "Copy Markdown Link", CtrlShift((VirtualKey)0xBE)),
    ];

    public static CommandContextItem Action(string title, string glyph, Action action, KeyChord? shortcut = null, ICommandResult? result = null) =>
        new(new AnonymousCommand(action) { Name = title, Icon = Glyph(glyph), Result = result ?? CommandResult.KeepOpen() })
        {
            Title = title,
            RequestedShortcut = shortcut ?? default,
        };

    /// <summary>An action that asks first (Raycast's confirmAlert); critical paints it red.</summary>
    public static CommandContextItem Confirm(string title, string glyph, string question, string description, Action action, bool critical = false, KeyChord? shortcut = null)
    {
        var run = new AnonymousCommand(action) { Name = title, Result = CommandResult.KeepOpen() };
        var ask = new AnonymousCommand(() => { })
        {
            Name = title,
            Icon = Glyph(glyph),
            Result = CommandResult.Confirm(new ConfirmationArgs { Title = question, Description = description, PrimaryCommand = run, IsPrimaryCommandCritical = critical }),
        };
        return new CommandContextItem(ask) { Title = title, IsCritical = critical, RequestedShortcut = shortcut ?? default };
    }

    /// <summary>Opens the settings page of a provider in Command Palette.</summary>
    public static OpenUrlCommand OpenSettings(string providerId) =>
        new($"x-cmdpal://settings/extension/{Uri.EscapeDataString(providerId)}") { Name = "Open Settings", Icon = Glyph(""), Result = CommandResult.Dismiss() };

    /// <summary>Empty-list item that sends the user to the settings.</summary>
    public static CommandItem SetupItem(string providerId, string what) => new(OpenSettings(providerId))
    {
        Title = $"Set up {what}",
        Subtitle = "Press Enter to open the settings of this extension",
        Icon = Glyph(""),
    };

    public static CommandItem ErrorItem(Exception ex, Action retry) => new(new AnonymousCommand(retry) { Name = "Try Again", Result = CommandResult.KeepOpen() })
    {
        Title = "Something went wrong",
        Subtitle = ex is ApiException or InvalidOperationException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}",
        Icon = Glyph(""),
    };

    public static CommandItem EmptyItem(string title, string subtitle = "", string glyph = "") => new()
    {
        Title = title,
        Subtitle = subtitle,
        Icon = Glyph(glyph),
    };

    public static DetailsElement Label(string key, string? value) => new()
    {
        Key = key,
        Data = new DetailsLink { Text = string.IsNullOrWhiteSpace(value) ? "–" : value },
    };

    public static DetailsElement Link(string key, string url, string? text = null) => new()
    {
        Key = key,
        Data = new DetailsLink(url, text ?? url),
    };

    public static DetailsElement Tags(string key, params ITag[] tags) => new() { Key = key, Data = new DetailsTags { Tags = tags } };

    public static DetailsElement Separator() => new() { Key = string.Empty, Data = new DetailsSeparator() };

    public static string Date(DateTimeOffset? value) => value is { } v ? v.ToLocalTime().ToString("g") : "–";

    /// <summary>"5 min ago", "yesterday", ... for list subtitles.</summary>
    public static string Ago(DateTimeOffset? value)
    {
        if (value is not { } v)
        {
            return string.Empty;
        }

        var span = DateTimeOffset.Now - v;
        return span.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)span.TotalMinutes} min ago",
            < 60 * 24 => $"{(int)span.TotalHours} h ago",
            < 60 * 24 * 2 => "yesterday",
            < 60 * 24 * 30 => $"{(int)span.TotalDays} days ago",
            _ => v.ToLocalTime().ToString("d"),
        };
    }

    public static IContextItem[] Join(params IEnumerable<IContextItem>[] groups) => [.. groups.SelectMany(g => g)];
}
