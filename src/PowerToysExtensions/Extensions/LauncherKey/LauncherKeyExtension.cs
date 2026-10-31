using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.LauncherKey;

[Guid("C8E879D1-D35B-4FB7-93DC-269705348DB5")]
public sealed partial class LauncherKeyExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new LauncherKeyProvider();
}

public sealed class LauncherKeySettings : SettingsBase
{
    private readonly ToggleSetting _enabled = new(
        "enabled",
        "Open Command Palette with the Windows key",
        "A short tap of the Windows key alone opens Command Palette instead of the Start menu. Win + another key (Win+E, Win+L …) keeps working; the Start menu opens with Ctrl+Esc. If Raycast also uses the Windows key, set Raycast to another hotkey (e.g. Alt+Space) – only one launcher can own the key.",
        false);

    public LauncherKeySettings()
        : base("launcher-key")
    {
        Settings.Add(_enabled);
        Load();
    }

    public bool Enabled
    {
        get => _enabled.Value;
        set
        {
            _enabled.Value = value;
            SaveSettings();
        }
    }
}

public sealed partial class LauncherKeyProvider : CommandProvider
{
    private readonly LauncherKeySettings _settings = new();
    private readonly CommandItem _toggle;

    public LauncherKeyProvider()
    {
        Id = "delido.launcher-key";
        DisplayName = "Launcher Key";
        Icon = new IconInfo(""); // Keyboard
        Settings = _settings.Settings;

        _toggle = new CommandItem(new AnonymousCommand(Toggle) { Id = "toggle-launcher-key", Name = "Toggle", Result = CommandResult.KeepOpen() })
        {
            Title = "Toggle Windows Key for Command Palette",
            Icon = Icon,
        };

        _settings.Changed += Apply;
        Apply();
    }

    public override ICommandItem[] TopLevelCommands() => [_toggle];

    public override ICommandItem? GetCommandItem(string id) => _toggle.Command?.Id == id ? _toggle : null;

    public override void Dispose()
    {
        WinKeyHook.Stop();
        base.Dispose();
    }

    private void Toggle()
    {
        _settings.Enabled = !_settings.Enabled;
        Apply();
        new ToastStatusMessage(_settings.Enabled
            ? "Windows key now opens Command Palette"
            : "Windows key opens the Start menu again").Show();
    }

    private void Apply()
    {
        if (_settings.Enabled)
        {
            WinKeyHook.Start();
        }
        else
        {
            WinKeyHook.Stop();
        }

        _toggle.Subtitle = _settings.Enabled
            ? "On – tapping Win opens Command Palette"
            : "Off – Win opens the Start menu";
    }
}
