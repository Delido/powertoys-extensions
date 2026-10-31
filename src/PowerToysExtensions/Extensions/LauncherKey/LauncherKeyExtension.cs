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
        "A short tap of the Windows key alone opens Command Palette (tap again to close it). Win + another key (Win+E, Win+L …) keeps working; the Start menu opens with Ctrl+Esc, or with Win while an administrator window is in front. Runs as its own small process (launcher-key.exe), so the key keeps working even while Command Palette restarts. If Raycast also uses the Windows key, set Raycast to another hotkey – only one launcher can own the key.",
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
    private readonly Timer _watchdog;

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

        // the helper is a separate process; start it again should it ever end
        _watchdog = new Timer(_ => Ensure(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public override ICommandItem[] TopLevelCommands() => [_toggle];

    public override ICommandItem? GetCommandItem(string id) => _toggle.Command?.Id == id ? _toggle : null;

    public override void Dispose()
    {
        // The helper keeps running on purpose: the Windows key must not depend on this process.
        _watchdog.Dispose();
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
        Ensure();
        _toggle.Subtitle = _settings.Enabled
            ? "On – tapping Win opens Command Palette"
            : "Off – Win opens the Start menu";
    }

    private void Ensure()
    {
        try
        {
            var running = KeyHelper.Running;
            if (_settings.Enabled && !running)
            {
                KeyHelper.Start();
            }
            else if (!_settings.Enabled && running)
            {
                KeyHelper.Stop();
            }
        }
        catch (Exception ex)
        {
            Log.Error("launcher key: helper", ex);
        }
    }
}

/// <summary>Helpers\launcher-key.exe (source in helpers/launcher-key): the keyboard hook in its own process.</summary>
internal static class KeyHelper
{
    private static string Exe => Path.Combine(Paths.App, "Helpers", "launcher-key.exe");

    public static bool Running
    {
        get
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("launcher-key");
            foreach (var process in processes)
            {
                process.Dispose();
            }

            return processes.Length > 0;
        }
    }

    public static void Start()
    {
        var folder = Paths.For("launcher-key");
        var result = Proc.Run(Exe, ["launch", "-settings", Path.Combine(folder, "settings.json"), "-log", Path.Combine(folder, "helper.log")], TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Log.Info($"launcher key: helper started {result.Output.Trim()}");
    }

    public static void Stop()
    {
        Proc.Run(Exe, ["stop"], TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Log.Info("launcher key: helper stopped");
    }
}
