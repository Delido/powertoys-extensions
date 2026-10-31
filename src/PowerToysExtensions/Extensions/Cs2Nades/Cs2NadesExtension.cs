using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.Cs2Nades;

[Guid("E1E42D05-6701-4E8B-9D0D-CA638E908459")]
public sealed partial class Cs2NadesExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new Cs2NadesProvider();
}

public sealed class Cs2NadesSettings : SettingsBase
{
    private readonly ToggleSetting _recommended = new("onlyRecommended", "Show only recommended lineups at start", "Recommended lineups are the ones csnades.gg marks as the essentials for a map. Ctrl+Shift+E switches in the list.", false);
    private readonly ToggleSetting _video = new("inlineVideo", "Show the video in Command Palette", "Converts the lineup video into an animated GIF with ffmpeg (about 5 s per lineup, cached afterwards). Command Palette cannot play videos itself.", true);
    private readonly TextSetting _ffmpeg = new("ffmpegPath", "ffmpeg path", "Only needed if ffmpeg is installed somewhere unusual. Leave empty to find it automatically.", string.Empty) { Placeholder = @"C:\Tools\ffmpeg\bin\ffmpeg.exe" };

    public Cs2NadesSettings()
        : base("cs2-nades")
    {
        Settings.Add(_recommended);
        Settings.Add(_video);
        Settings.Add(_ffmpeg);
        Load();
    }

    public bool OnlyRecommended => _recommended.Value;

    public bool InlineVideo => _video.Value;

    public string FfmpegPath => _ffmpeg.Value ?? string.Empty;
}

public sealed partial class Cs2NadesProvider : CommandProvider
{
    private readonly ICommandItem[] _commands;

    public Cs2NadesProvider()
    {
        var settings = new Cs2NadesSettings();
        Id = "delido.cs2-nades";
        DisplayName = "CS2 Nades";
        Icon = Ui.Glyph("\uE945");
        Settings = settings.Settings;
        _commands = [new CommandItem(new NadesPage(settings)) { Title = "CS2 Nades", Subtitle = "csnades.gg" }];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem? GetCommandItem(string id) => _commands.FirstOrDefault(c => c.Command?.Id == id);
}

internal sealed partial class NadesPage : AsyncListPage
{
    private static readonly string LastMapFile = Path.Combine(Paths.For("cs2-nades"), "last-map.txt");
    private readonly Cs2NadesSettings _settings;
    private readonly ChoiceFilters _maps;
    private readonly ConcurrentDictionary<string, Nade[]> _loaded = new();
    private string _side = "all";
    private bool _recommended;

    public NadesPage(Cs2NadesSettings settings)
    {
        _settings = settings;
        _recommended = settings.OnlyRecommended;
        Id = "cs2nades.nades";
        Name = "Open";
        Icon = Ui.Glyph("\uE945");
        PlaceholderText = "Search target or position, e.g. Jungle, Window, CT …";
        ShowDetails = true;
        _maps = new ChoiceFilters([
            .. CsNades.PremierMaps.Select(m => (m.Slug, m.Name, (IconInfo?)null)),
            .. CsNades.OtherMaps.Select(m => (m.Slug, $"{m.Name} (not in Premier)", (IconInfo?)null))]);
        try
        {
            if (File.Exists(LastMapFile) && File.ReadAllText(LastMapFile).Trim() is { Length: > 0 } last)
            {
                _maps.CurrentFilterId = last;
            }
        }
        catch (IOException)
        {
            // first start
        }

        Filters = _maps;
        _maps.Changed += () =>
        {
            File.WriteAllText(LastMapFile, _maps.CurrentFilterId);
            Reload();
        };
        UpdateTitle();
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        var map = _maps.CurrentFilterId;
        UpdateTitle();
        if (!_loaded.TryGetValue(map, out var nades))
        {
            try
            {
                nades = _loaded[map] = await CsNades.Load(map, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && CsNades.Cached(map) is { } cached)
            {
                // offline or site changed: last known lineups are better than nothing
                Log.Error("cs2-nades load", ex);
                nades = cached;
            }
        }

        var items = new List<IListItem>();
        foreach (var (type, _, title) in CsNades.Types)
        {
            var group = nades
                .Where(n => n.Type == type && (_side == "all" || n.Team == _side) && (!_recommended || n.Recommended))
                .OrderByDescending(n => n.Recommended)
                .ToList();
            items.AddRange(group.Select(n => Item(n, $"{title} ({group.Count})")));
        }

        return new([.. items]);
    }

    protected override ICommandItem? Empty() =>
        new CommandItem(new AnonymousCommand(ToggleRecommended) { Name = "Show All", Result = CommandResult.KeepOpen() })
        {
            Title = "No lineups",
            Subtitle = _recommended ? "This map has no recommended lineups – Enter shows all" : string.Empty,
            Icon = Ui.Glyph("\uE721"),
        };

    private void UpdateTitle()
    {
        var side = _side switch { "t" => "T Side", "ct" => "CT Side", _ => "T + CT" };
        Title = $"{CsNades.MapName(_maps.CurrentFilterId)} – {side}{(_recommended ? " · recommended only" : string.Empty)}";
    }

    private void CycleSide()
    {
        _side = _side switch { "all" => "t", "t" => "ct", _ => "all" };
        Reload();
    }

    private void ToggleRecommended()
    {
        _recommended = !_recommended;
        Reload();
    }

    /// <summary>Command Palette image hint: scale down to the pane instead of filling it zoomed in.</summary>
    internal static string Fit(string url) => $"{url}?--x-cmdpal-fit=fit";

    private static Tag TeamTag(string team) => team == "t" ? Ui.Yellow("T") : Ui.Blue("CT");

    private ListItem Item(Nade n, string section)
    {
        var nextSide = _side switch { "all" => "T Side", "t" => "CT Side", _ => "T + CT" };
        var body = string.Join("\n\n", new[]
        {
            n.Lineup.Length > 0 ? $"![Lineup]({Fit(n.Lineup)})" : string.Empty,
            n.Thumbnail.Length > 0 ? $"![Landing spot]({Fit(n.Thumbnail)})" : string.Empty,
            $"_Enter shows the video · Source: [csnades.gg]({n.Url})_",
        }.Where(s => s.Length > 0));

        return new ListItem(new NadeVideoPage(n, _settings))
        {
            Title = n.To,
            Subtitle = $"from {n.From}",
            Icon = Ui.Glyph(n.Recommended ? "\uE735" : "\uEA3A"),
            Section = section,
            Tags = n.Recommended ? [TeamTag(n.Team), Ui.Green("Recommended")] : [TeamTag(n.Team)],
            Details = new Details
            {
                Title = n.To,
                Body = body,
                Size = ContentSize.Large,
                Metadata =
                [
                    Ui.Label("Target", n.To),
                    Ui.Label("Throw From", n.From),
                    Ui.Tags("Side", n.Recommended ? [TeamTag(n.Team), Ui.Green("Recommended")] : [TeamTag(n.Team)]),
                    Ui.Separator(),
                    Ui.Label("Throw", CsNades.Technique(n.Technique)),
                    Ui.Label("Movement", CsNades.Movement(n.Movement)),
                    Ui.Label("Precision", CsNades.Precision(n.Precision)),
                    Ui.Separator(),
                    Ui.Link("Video", n.VideoHq, "Play HQ video"),
                    Ui.Link("Source", n.Url, "csnades.gg"),
                ],
            },
            MoreCommands =
            [
                Ui.OpenUrl(n.VideoHq, "Play HQ Video in Browser", Ui.Ctrl(VirtualKey.Enter)),
                Ui.OpenUrl(n.Url, "Open on csnades.gg", Ui.Ctrl(VirtualKey.O)),
                Ui.OpenUrl(n.VideoLq, "Play Low Quality Video", Ui.Ctrl(VirtualKey.L)),
                Ui.OpenUrl(n.Lineup, "Open Lineup Picture", Ui.Ctrl(VirtualKey.I)),
                Ui.Copy(n.Url, "Copy Link", Ui.CtrlShift(VirtualKey.C)),
                Ui.Action($"Show {nextSide}", "\uE716", CycleSide, Ui.CtrlShift(VirtualKey.T)),
                Ui.Action(_recommended ? "Show All Lineups" : "Show Only Recommended", "\uE734", ToggleRecommended, Ui.CtrlShift(VirtualKey.E)),
                Ui.Action("Reload", "\uE72C", () =>
                {
                    _loaded.TryRemove(_maps.CurrentFilterId, out _);
                    Reload();
                }, Ui.Ctrl(VirtualKey.R)),
            ],
        };
    }
}

/// <summary>Shows the lineup video as GIF (converted on first open), with the lineup picture below.</summary>
internal sealed partial class NadeVideoPage : ContentPage
{
    private readonly Nade _nade;
    private readonly Cs2NadesSettings _settings;
    private readonly MarkdownContent _content = new();
    private bool _started;

    public NadeVideoPage(Nade nade, Cs2NadesSettings settings)
    {
        _nade = nade;
        _settings = settings;
        Name = "Show Video";
        Title = $"{nade.To} from {nade.From}";
        Icon = Ui.Glyph("\uE714");
        Commands =
        [
            Ui.OpenUrl(nade.VideoHq, "Play HQ Video in Browser", Ui.Ctrl(VirtualKey.Enter)),
            Ui.OpenUrl(nade.Url, "Open on csnades.gg", Ui.Ctrl(VirtualKey.O)),
            Ui.OpenUrl(nade.Lineup, "Open Lineup Picture", Ui.Ctrl(VirtualKey.I)),
            Ui.Copy(nade.Url, "Copy Link", Ui.CtrlShift(VirtualKey.C)),
        ];
        Details = new Details
        {
            Title = nade.To,
            Metadata =
            [
                Ui.Label("Map", CsNades.MapName(nade.Map)),
                Ui.Label("Type", CsNades.TypeTitle(nade.Type)),
                Ui.Label("Throw From", nade.From),
                Ui.Label("Throw", CsNades.Technique(nade.Technique)),
                Ui.Label("Movement", CsNades.Movement(nade.Movement)),
                Ui.Label("Precision", CsNades.Precision(nade.Precision)),
                Ui.Link("Source", nade.Url, "csnades.gg"),
            ],
        };
    }

    public override IContent[] GetContent()
    {
        if (!_started)
        {
            _started = true;
            _ = Prepare();
        }

        return [_content];
    }

    private void Show(string video) =>
        _content.Body = $"{video}\n\n{(_nade.Lineup.Length > 0 ? $"![Lineup]({NadesPage.Fit(_nade.Lineup)})" : string.Empty)}\n\n_Source: [csnades.gg]({_nade.Url})_";

    private async Task Prepare()
    {
        if (!_settings.InlineVideo)
        {
            Show("_Video in Command Palette is switched off – Ctrl+Enter plays it in the browser._");
            return;
        }

        if (NadeVideo.Cached(_nade) is { } cached)
        {
            Show($"![Video]({NadesPage.Fit(NadeVideo.FileUrl(cached))})");
            return;
        }

        var ffmpeg = NadeVideo.FindFfmpeg(_settings.FfmpegPath);
        if (ffmpeg is null)
        {
            Show("**ffmpeg is needed to show the video here.** Open the actions (Ctrl+K) and pick *Install ffmpeg*, or press Ctrl+Enter to play it in the browser.");
            Commands = [new CommandContextItem(new AnonymousCommand(() => Status.Start("Installing ffmpeg with winget …", "ffmpeg installed – open the lineup again", NadeVideo.InstallFfmpeg)) { Name = "Install ffmpeg", Result = CommandResult.KeepOpen() }) { Title = "Install ffmpeg" }, .. Commands];
            return;
        }

        Show("_Preparing the video …_");
        IsLoading = true;
        try
        {
            var gif = await NadeVideo.Make(_nade, ffmpeg, CancellationToken.None);
            Show($"![Video]({NadesPage.Fit(NadeVideo.FileUrl(gif))})");
        }
        catch (Exception ex)
        {
            Log.Error("cs2-nades video", ex);
            Show($"_The video could not be prepared: {ex.Message}_");
        }
        finally
        {
            IsLoading = false;
        }
    }
}
