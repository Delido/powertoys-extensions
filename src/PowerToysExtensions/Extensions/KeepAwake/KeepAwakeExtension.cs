using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.KeepAwake;

[Guid("AED9D9F3-C822-4EC2-9C30-B632C5293A98")]
public sealed partial class KeepAwakeExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new KeepAwakeProvider();
}

public sealed partial class KeepAwakeProvider : CommandProvider
{
    private static readonly IconInfo AwakeIcon = new(""); // Brightness
    private static readonly IconInfo SleepIcon = new(""); // QuietHours

    private readonly KeepAwakeSettings _settings = new();
    private readonly CommandItem _main;
    private readonly CommandItem _toggle;
    private readonly CommandItem _band;

    public KeepAwakeProvider()
    {
        Id = "delido.keep-awake";
        DisplayName = "Keep Awake";
        Icon = AwakeIcon;
        Settings = _settings.Settings;

        _main = new CommandItem(new KeepAwakePage(_settings)) { Title = "Keep Awake" };
        _toggle = new CommandItem(new ToggleAwakeCommand(_settings)) { Title = "Toggle Keep Awake" };
        _band = new CommandItem(new ToggleAwakeCommand(_settings) { Id = "keepawake.keep-awake-band" });

        AwakeEngine.Changed += Refresh;
        AwakeEngine.Resume();
        Refresh();
    }

    public override ICommandItem[] TopLevelCommands() => [_main, _toggle];

    public override ICommandItem[]? GetDockBands() => [_band];

    public override ICommandItem? GetCommandItem(string id) =>
        TopLevelCommands().Concat([_band]).FirstOrDefault(i => i.Command?.Id == id);

    public override void Dispose()
    {
        AwakeEngine.Changed -= Refresh;
        base.Dispose();
    }

    private void Refresh()
    {
        var state = AwakeEngine.Current;
        var status = state is null ? "Off" : $"On {AwakeEngine.Describe(state)}";
        _main.Subtitle = status;
        _main.Icon = state is null ? SleepIcon : AwakeIcon;
        _toggle.Subtitle = state is null ? "Switch on with the default duration" : $"{status} – switch off";
        _band.Title = state is null ? "Awake off" : "Awake";
        _band.Subtitle = state is null ? string.Empty : AwakeEngine.Describe(state);
        _band.Icon = state is null ? SleepIcon : AwakeIcon;
    }
}

internal sealed partial class ToggleAwakeCommand : InvokableCommand
{
    private readonly KeepAwakeSettings _settings;

    public ToggleAwakeCommand(KeepAwakeSettings settings)
    {
        _settings = settings;
        Id = "keepawake.toggle-keep-awake";
        Name = "Toggle";
        Icon = new IconInfo("");
    }

    public override ICommandResult Invoke()
    {
        if (AwakeEngine.Current is not null)
        {
            AwakeEngine.Stop();
            return CommandResult.ShowToast("Keep Awake is off");
        }

        AwakeEngine.Start(_settings.DefaultDuration, _settings.Teams, _settings.Display);
        return CommandResult.ShowToast($"Keep Awake is on {AwakeEngine.Describe(AwakeEngine.Current!)}");
    }
}

internal sealed partial class KeepAwakePage : ListPage
{
    private static readonly (string Title, int Minutes)[] Durations =
        [("Indefinitely", 0), ("30 minutes", 30), ("1 hour", 60), ("2 hours", 120), ("4 hours", 240), ("8 hours", 480)];

    private readonly KeepAwakeSettings _settings;

    public KeepAwakePage(KeepAwakeSettings settings)
    {
        _settings = settings;
        Id = "keepawake.keep-awake";
        Name = "Open";
        Title = "Keep Awake";
        Icon = new IconInfo("");
        PlaceholderText = "Choose how long to stay awake";
        AwakeEngine.Changed += () => RaiseItemsChanged();
    }

    public override IListItem[] GetItems()
    {
        var items = new List<IListItem>();
        if (AwakeEngine.Current is { } state)
        {
            items.Add(new ListItem(new AnonymousCommand(AwakeEngine.Stop) { Name = "Stop", Result = CommandResult.KeepOpen() })
            {
                Title = "Stop Keep Awake",
                Subtitle = $"Running {AwakeEngine.Describe(state)}" + (state.Teams ? " · Teams stays available" : string.Empty),
                Icon = new IconInfo(""),
                Section = "Status",
            });
        }

        foreach (var (title, minutes) in Durations)
        {
            TimeSpan? duration = minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;
            items.Add(new ListItem(new AnonymousCommand(() => AwakeEngine.Start(duration, _settings.Teams, _settings.Display))
            {
                Name = "Start",
                Result = CommandResult.ShowToast(minutes > 0 ? $"Keep Awake for {title}" : "Keep Awake indefinitely"),
            })
            {
                Title = title,
                Subtitle = minutes > 0 ? $"until {DateTime.Now.AddMinutes(minutes):HH:mm}" : "until you stop it",
                Icon = new IconInfo(""),
                Section = "Start",
                MoreCommands =
                [
                    new CommandContextItem(new AnonymousCommand(() => AwakeEngine.Start(duration, !_settings.Teams, _settings.Display))
                    {
                        Name = _settings.Teams ? "Start without Teams presence" : "Start with Teams presence",
                        Result = CommandResult.ShowToast("Keep Awake started"),
                    }),
                ],
            });
        }

        return [.. items];
    }
}
