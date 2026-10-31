using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.KeepAwake;

public sealed class KeepAwakeSettings : SettingsBase
{
    private readonly ToggleSetting _teams = new(
        "teamsPresence",
        "Keep the Teams status available",
        "Presses the unused F15 key after 60 seconds without input so Teams (and other apps) do not switch to Away. This also prevents the automatic screen lock – lock manually when you leave.",
        true);

    private readonly ToggleSetting _display = new(
        "keepDisplayOn",
        "Keep the display on",
        "Otherwise only sleep is prevented and the display may turn off",
        true);

    private readonly ChoiceSetSetting _duration = new(
        "defaultDuration",
        "Default duration",
        "Duration used by \"Toggle Keep Awake\"",
        Choices(("Indefinitely", "0"), ("30 minutes", "30"), ("1 hour", "60"), ("2 hours", "120"), ("4 hours", "240"), ("8 hours", "480")));

    public KeepAwakeSettings()
        : base("keep-awake")
    {
        Settings.Add(_teams);
        Settings.Add(_display);
        Settings.Add(_duration);
        Load();
    }

    public bool Teams => _teams.Value;

    public bool Display => _display.Value;

    public TimeSpan? DefaultDuration => int.TryParse(_duration.Value, out var m) && m > 0 ? TimeSpan.FromMinutes(m) : null;
}
