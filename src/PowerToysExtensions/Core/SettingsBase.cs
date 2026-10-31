using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerToysExtensions.Core;

/// <summary>Settings of one extension, stored as JSON in the package data folder.</summary>
public abstract class SettingsBase : JsonSettingsManager
{
    protected SettingsBase(string extension)
    {
        FilePath = Path.Combine(Paths.For(extension), "settings.json");
    }

    /// <summary>Raised after the user saved the settings page.</summary>
    public event Action? Changed;

    /// <summary>Call at the end of the subclass constructor, after all settings were added.</summary>
    protected void Load()
    {
        LoadSettings();
        Settings.SettingsChanged += (_, _) =>
        {
            SaveSettings();
            Changed?.Invoke();
        };
    }

    protected static List<ChoiceSetSetting.Choice> Choices(params (string Title, string Value)[] choices) =>
        [.. choices.Select(c => new ChoiceSetSetting.Choice(c.Title, c.Value))];
}
