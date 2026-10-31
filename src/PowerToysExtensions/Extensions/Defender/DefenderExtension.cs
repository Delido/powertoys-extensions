using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.Defender;

[Guid("94557F86-907B-49E8-83BC-5DC511C0199C")]
public sealed partial class DefenderExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new DefenderProvider();
}

public sealed class DefenderSettings : SettingsBase
{
    private readonly TextSetting _tenantName = new("tenantName", "Mandant (Anzeigename)", "Name des Mandanten im Report, z. B. Contoso GmbH", string.Empty);
    private readonly TextSetting _tenant = new("tenantId", "Tenant-ID", "Verzeichnis-ID (Tenant) der App-Registrierung", string.Empty);
    private readonly TextSetting _client = new("clientId", "Client-ID", "Anwendungs-ID (Client) der App-Registrierung mit der Berechtigung Vulnerability.Read.All (WindowsDefenderATP, Anwendung). Das Client-Secret wird verschlüsselt mit deinem Defender-Passwort gespeichert.", string.Empty);
    private readonly TextSetting _api = new("apiBase", "API-Adresse", "Nur ändern, wenn ein regionaler Endpunkt nötig ist, z. B. https://eu.api.security.microsoft.com", DefenderReport.DefaultApiBase);
    private readonly TextSetting _folder = new("reportFolder", "Report-Ordner", "Optional: Ordner für die HTML-Reports. Leer = Datenordner der Erweiterung.", string.Empty);
    private readonly ChoiceSetSetting _autoLock = new("autoLock", "Automatisch sperren nach", "Die Zugangsdaten werden nach dieser Zeit ohne Nutzung wieder gesperrt", Choices(("5 Minuten", "5"), ("1 Minute", "1"), ("15 Minuten", "15"), ("1 Stunde", "60")));
    private readonly ToggleSetting _demo = new("demoMode", "Demodaten", "Report mit Beispieldaten erzeugen statt Defender abzufragen (zum Ausprobieren)", false);

    public DefenderSettings()
        : base("defender")
    {
        Settings.Add(_tenantName);
        Settings.Add(_tenant);
        Settings.Add(_client);
        Settings.Add(_api);
        Settings.Add(_folder);
        Settings.Add(_autoLock);
        Settings.Add(_demo);
        Load();
    }

    public string TenantName => string.IsNullOrWhiteSpace(_tenantName.Value) ? "Defender" : _tenantName.Value.Trim();

    public string TenantId => _tenant.Value?.Trim() ?? string.Empty;

    public string ClientId => _client.Value?.Trim() ?? string.Empty;

    public string ApiBase => string.IsNullOrWhiteSpace(_api.Value) ? DefenderReport.DefaultApiBase : _api.Value.Trim();

    public string ReportFolder => _folder.Value?.Trim().Trim('"') ?? string.Empty;

    public TimeSpan AutoLock => TimeSpan.FromMinutes(int.TryParse(_autoLock.Value, out var m) ? m : 5);

    public bool Demo => _demo.Value;

    public bool Configured => Guid.TryParse(TenantId, out _) && Guid.TryParse(ClientId, out _);
}

public sealed class DefenderContext
{
    public DefenderContext()
    {
        Settings = new DefenderSettings();
        Store = new DefenderStore(() => Settings.ReportFolder);
        Vault = new Vault("defender", () => Settings.AutoLock);
        Gate = new VaultGate(Vault, "Defender", "Client-Secret", "Wert (nicht die Secret-ID) aus Entra → App-Registrierung → Zertifikate & Geheimnisse", async secret => await DefenderReport.Token(Credentials(secret)));
    }

    public DefenderSettings Settings { get; }

    public DefenderStore Store { get; }

    public Vault Vault { get; }

    public VaultGate Gate { get; }

    public DefenderCredentials Credentials(string secret)
    {
        if (!Settings.Configured)
        {
            throw new InvalidOperationException("Zuerst Tenant-ID und Client-ID (GUIDs) in den Einstellungen eintragen");
        }

        return new DefenderCredentials(Settings.TenantId, Settings.ClientId, secret.Trim(), Settings.ApiBase);
    }

    public ICommandItem? NoData(Exception ex, Action reload) => ex is FileNotFoundException
        ? new CommandItem(new RefreshPage(this)) { Title = "Noch kein Report", Subtitle = "Enter: Schwachstellen-Report aktualisieren", Icon = Ui.Glyph("") }
        : Ui.ErrorItem(ex, reload);
}

public sealed partial class DefenderProvider : CommandProvider
{
    public const string ProviderId = "delido.defender";
    private readonly ICommandItem[] _commands;

    public DefenderProvider()
    {
        var context = new DefenderContext();
        Id = ProviderId;
        DisplayName = "Defender Schwachstellen";
        Icon = Glyphs.Shield;
        Settings = context.Settings.Settings;
        _commands =
        [
            new CommandItem(new SearchPage(context)) { Title = "Schwachstellen durchsuchen", Subtitle = "Defender · CVEs, Software und Geräte inklusive Fundstellen" },
            new CommandItem(new TopActionsPage(context)) { Title = "Top-Maßnahmen", Subtitle = "Defender · welche Maßnahme entfernt das meiste Risiko?" },
            new CommandItem(new RefreshPage(context)) { Title = "Schwachstellen-Report aktualisieren", Subtitle = "Defender · aktuelle Daten holen und den Report neu erzeugen" },
            new CommandItem(new OpenReportCommand(context.Store)) { Title = "Schwachstellen-Report öffnen", Subtitle = "Defender · aktuellen HTML-Report im Browser öffnen" },
            new CommandItem(new CredentialsPage(context)) { Title = "Defender-Zugangsdaten verwalten", Subtitle = "Defender · Secret hinterlegen, erneuern, Passwort ändern" },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem? GetCommandItem(string id) => _commands.FirstOrDefault(c => c.Command?.Id == id);
}

internal static class Glyphs
{
    public static readonly IconInfo Shield = Ui.Glyph("");
    public static readonly IconInfo Software = Ui.Glyph("");
    public static readonly IconInfo Cve = Ui.Glyph("");
    public static readonly IconInfo Device = Ui.Glyph("");
    public static readonly IconInfo Report = Ui.Glyph("");
}

/// <summary>Details whose markdown is only built when Command Palette shows it.</summary>
internal sealed partial class LazyDetails : Details
{
    private readonly Func<string> _build;
    private bool _built;

    public LazyDetails(string title, Func<string> build)
    {
        _build = build;
        Title = title;
        Size = ContentSize.Large;
    }

    public override string Body
    {
        get
        {
            if (!_built)
            {
                _built = true;
                try
                {
                    base.Body = _build();
                }
                catch (Exception ex)
                {
                    base.Body = $"_Details nicht verfügbar: {ex.Message}_";
                }
            }

            return base.Body;
        }
        set => base.Body = value;
    }
}

internal static class Items
{
    public static ListItem Software(DefenderContext context, DefenderModel m, SoftwareAgg a, string? title = null, string section = "") =>
        new(ReportLink(context, $"sw={Uri.EscapeDataString(a.Key)}"))
        {
            Title = title ?? a.Name,
            Subtitle = a.Action,
            Icon = Glyphs.Software,
            Section = section,
            Tags = [.. SeverityTags(a.SevCves[3], a.SevCves[2], a.MaxE, a.Recent, m), Ui.Tag($"{DefenderModel.Fmt(a.DevCount)} Geräte"), Ui.Gray(DefenderModel.Compact(a.Score))],
            Details = new LazyDetails(a.Name, () => m.SoftwareMarkdown(a)),
            MoreCommands =
            [
                Ui.Copy($"{a.Name}: {a.Action}", "Maßnahme kopieren", Ui.CtrlShift(VirtualKey.C)),
                CopyLater("Betroffene Geräte kopieren", () => m.DeviceNames(o => m.Data.F[o + DefenderData.Sw] == a.I)),
            ],
        };

    public static ListItem Cve(DefenderContext context, DefenderModel m, CveAgg b, string section = "")
    {
        var c = b.Cve;
        var software = string.Join(", ", b.Sws.Select(m.SoftwareNameOf));
        var tags = new List<ITag> { SeverityTag(c.S) };
        if (c.E > 0)
        {
            tags.Add(Ui.Red($"⚠ {DefenderModel.ExpShort[c.E]}"));
        }

        if (c.C > 0)
        {
            tags.Add(Ui.Tag($"CVSS {c.C:0.0}"));
        }

        if (b.Recent > 0)
        {
            tags.Add(Ui.Blue($"+{DefenderModel.Fmt(b.Recent)}"));
        }

        tags.Add(Ui.Tag($"{DefenderModel.Fmt(b.DevCount)} Geräte"));
        return new ListItem(ReportLink(context, $"cve={Uri.EscapeDataString(c.Id)}"))
        {
            Title = c.Id,
            Subtitle = software,
            Icon = Glyphs.Cve,
            Section = section,
            Tags = [.. tags],
            Details = new LazyDetails(c.Id, () => m.CveMarkdown(b)),
            MoreCommands =
            [
                Ui.OpenUrl(DefenderModel.CveUrl(c.Id), "In Defender öffnen", Ui.Ctrl(VirtualKey.O)),
                Ui.OpenUrl(DefenderModel.NvdUrl(c.Id), "NVD öffnen", Ui.CtrlShift(VirtualKey.O)),
                Ui.Copy(c.Id, "CVE kopieren", Ui.CtrlShift(VirtualKey.C)),
                CopyLater("Betroffene Geräte kopieren", () => m.DeviceNames(o => m.Data.F[o + DefenderData.Cve] == b.I)),
            ],
        };
    }

    public static ListItem Device(DefenderContext context, DefenderModel m, DeviceAgg d, string section = "")
    {
        var device = d.Device;
        var commands = new List<IContextItem>();
        if (device.Id is { Length: > 0 } id && !m.IsDemo)
        {
            commands.Add(Ui.OpenUrl(DefenderModel.DeviceUrl(id), "In Defender öffnen", Ui.Ctrl(VirtualKey.O)));
        }

        commands.Add(Ui.Copy(device.N, "Gerätename kopieren", Ui.CtrlShift(VirtualKey.C)));
        commands.Add(CopyLater("Fundstellen kopieren", () => m.DevicePaths(d)));
        return new ListItem(ReportLink(context, $"dev={Uri.EscapeDataString(device.N)}"))
        {
            Title = device.N,
            Subtitle = $"{device.G} · {device.Os}",
            Icon = Glyphs.Device,
            Section = section,
            Tags = [.. SeverityTags(d.Sev[3], d.Sev[2], d.MaxE, d.Recent, m), Ui.Tag($"{DefenderModel.Fmt(d.N)} Funde")],
            Details = new LazyDetails(device.N, () => m.DeviceMarkdown(d)),
            MoreCommands = [.. commands],
        };
    }

    private static Tag SeverityTag(int s) => s switch
    {
        3 => Ui.Red(DefenderModel.SevNames[3]),
        2 => Ui.Orange(DefenderModel.SevNames[2]),
        1 => Ui.Yellow(DefenderModel.SevNames[1]),
        _ => Ui.Gray(DefenderModel.SevNames[0]),
    };

    private static IEnumerable<ITag> SeverityTags(int critical, int high, int maxExploit, int recent, DefenderModel m)
    {
        yield return critical > 0 ? Ui.Red($"{critical} krit.") : Ui.Gray("0 krit.");
        yield return high > 0 ? Ui.Orange($"{high} hoch") : Ui.Gray("0 hoch");
        if (maxExploit > 0)
        {
            yield return Ui.Red($"⚠ {DefenderModel.ExpShort[maxExploit]}");
        }

        if (recent > 0)
        {
            yield return Ui.Blue($"+{DefenderModel.Fmt(recent)} {m.NewLabel}");
        }
    }

    private static AnonymousCommand ReportLink(DefenderContext context, string hash) => new(() =>
    {
        try
        {
            context.Store.Open(hash);
        }
        catch (Exception ex)
        {
            Status.Error(ex.Message);
        }
    })
    { Name = "Im Report öffnen", Icon = Ui.Glyph(""), Result = CommandResult.Dismiss() };

    /// <summary>Copy action whose text is only computed when it is used.</summary>
    private static CommandContextItem CopyLater(string title, Func<string> text) => Ui.Action(title, "", () =>
    {
        ClipboardHelper.SetText(text());
        Status.Info("In die Zwischenablage kopiert");
    });
}

internal sealed partial class SearchPage : SearchListPage
{
    private readonly DefenderContext _context;

    public SearchPage(DefenderContext context)
    {
        _context = context;
        Id = "defender.search";
        Name = "Durchsuchen";
        Title = "Schwachstellen durchsuchen";
        Icon = Glyphs.Shield;
        PlaceholderText = "CVE, Software, Gerät oder Pfad …";
        ShowDetails = true;
        Debounce = TimeSpan.FromMilliseconds(150);
    }

    protected override Task<Batch> Search(string query, int page, CancellationToken token) => Task.Run(() =>
    {
        var m = new DefenderModel(_context.Store.Load());
        PlaceholderText = $"CVE, Software, Gerät oder Pfad … ({DefenderModel.Fmt(m.Cves.Length)} CVEs, {DefenderModel.Fmt(m.Devices.Length)} Geräte)";
        var limit = query.Length > 0 ? 50 : 8;
        bool Has(string text) => text.Contains(query, StringComparison.OrdinalIgnoreCase);
        var software = query.Length > 0 ? m.Software.Where(a => Has($"{a.Name} {a.Key} {a.Action}")).ToArray() : m.Software;
        var cves = query.Length > 0 ? m.Cves.Where(b => Has(b.Cve.Id)).ToArray() : m.Cves;
        var pathHits = query.Length >= 3 ? m.DevicesWithPath(query) : [];
        var devices = query.Length > 0
            ? m.Devices.Where(d => Has($"{d.Device.N} {d.Device.G} {d.Device.Os} {string.Join(' ', d.Device.T)}") || pathHits.Contains(d.I)).ToArray()
            : m.Devices;

        string Count(int shown, int total) => shown < total ? $"{DefenderModel.Fmt(shown)} von {DefenderModel.Fmt(total)}" : DefenderModel.Fmt(total);
        var swSection = query.Length > 0 ? $"Software · {Count(Math.Min(limit, software.Length), software.Length)}" : $"Wichtigste Maßnahmen · Stand {m.Generated.ToLocalTime():g}";
        var cveSection = $"{(query.Length > 0 ? "CVEs" : "Wichtigste CVEs")} · {Count(Math.Min(limit, cves.Length), cves.Length)}";
        var devSection = $"{(query.Length > 0 ? "Geräte" : "Am stärksten betroffene Geräte")} · {Count(Math.Min(limit, devices.Length), devices.Length)}";
        return new Batch([
            .. software.Take(limit).Select(a => Items.Software(_context, m, a, section: swSection)),
            .. cves.Take(limit).Select(b => Items.Cve(_context, m, b, cveSection)),
            .. devices.Take(limit).Select(d => Items.Device(_context, m, d, devSection)),
        ]);
    }, token);

    protected override ICommandItem? Empty(string query)
    {
        try
        {
            _context.Store.Load();
            return Ui.EmptyItem("Keine Treffer", "Gesucht wird in CVE-IDs, Software, Geräten, Gruppen, Tags und Pfaden.", "");
        }
        catch (Exception ex)
        {
            return _context.NoData(ex, Reload);
        }
    }
}

internal sealed partial class TopActionsPage : AsyncListPage
{
    private const string All = "__alle__";
    private readonly DefenderContext _context;
    private ChoiceFilters? _filters;
    private DefenderData? _shownData;

    public TopActionsPage(DefenderContext context)
    {
        _context = context;
        Id = "defender.top-actions";
        Name = "Öffnen";
        Title = "Top-Maßnahmen";
        Icon = Glyphs.Shield;
        PlaceholderText = "Software oder Maßnahme filtern …";
        ShowDetails = true;
        MaxAge = TimeSpan.FromMinutes(1);
    }

    protected override Task<Batch> Load(int page, CancellationToken token) => Task.Run(() =>
    {
        var data = _context.Store.Load();
        if (!ReferenceEquals(data, _shownData))
        {
            // new report: the device groups may have changed
            _shownData = data;
            var filters = new ChoiceFilters([(All, "Alle Gruppen"), .. data.Groups.Select(g => (g, g))]);
            filters.Changed += Reload;
            _filters = filters;
            Filters = filters;
        }

        var group = _filters?.CurrentFilterId is { } id && id != All ? id : null;
        var m = new DefenderModel(data, group);
        var section = $"Maßnahmen nach Priorität{(group is null ? string.Empty : $" · {group}")} · {DefenderModel.Fmt(m.Count)} Funde · Stand {m.Generated.ToLocalTime():g}";
        return new Batch([.. m.Software.Select((a, i) => Items.Software(_context, m, a, $"{i + 1}. {a.Name}", section))]);
    }, token);

    protected override ICommandItem? Empty()
    {
        try
        {
            _context.Store.Load();
            return new CommandItem(new OpenReportCommand(_context.Store)) { Title = "Keine offenen Funde", Subtitle = "Enter: Report öffnen", Icon = Ui.Glyph("") };
        }
        catch (Exception ex)
        {
            return _context.NoData(ex, Reload);
        }
    }
}

/// <summary>Unlock (or demo) and start a new report run.</summary>
internal sealed partial class RefreshPage : ListPage
{
    private readonly DefenderContext _context;

    public RefreshPage(DefenderContext context)
    {
        _context = context;
        Id = "defender.refresh-report";
        Name = "Öffnen";
        Title = "Schwachstellen-Report aktualisieren";
        Icon = Ui.Glyph("");
    }

    public override IListItem[] GetItems()
    {
        var settings = _context.Settings;
        var items = new List<IListItem>();
        if (settings.Demo)
        {
            items.Add(new ListItem(new ProgressPage(_context, demo: true, () => RaiseItemsChanged())) { Title = "Demo-Report erzeugen", Subtitle = "Demomodus: Beispieldaten, keine Verbindung zu Defender", Icon = Ui.Glyph("") });
        }
        else if (!settings.Configured)
        {
            items.Add(new ListItem(Ui.SetupItem(DefenderProvider.ProviderId, "Defender (Tenant-ID und Client-ID)")));
        }
        else if (_context.Gate.Items(() => RaiseItemsChanged()) is { } gate)
        {
            items.AddRange(gate);
        }
        else
        {
            items.Add(new ListItem(new ProgressPage(_context, demo: false, () => RaiseItemsChanged()))
            {
                Title = DefenderReport.IsRunning ? "Aktualisierung läuft …" : "Report jetzt aktualisieren",
                Subtitle = $"Mandant: {settings.TenantName}",
                Icon = Ui.Glyph(""),
                MoreCommands = _context.Gate.SecurityActions(() => RaiseItemsChanged()),
            });
        }

        if (!settings.Demo)
        {
            // try everything without Defender access; the next real run replaces it
            items.Add(new ListItem(new ProgressPage(_context, demo: true, () => RaiseItemsChanged())) { Title = "Demo-Report erzeugen", Subtitle = "Beispieldaten zum Ausprobieren, ohne Verbindung zu Defender", Icon = Ui.Glyph("") });
        }

        try
        {
            var m = new DefenderModel(_context.Store.Load());
            items.Add(new ListItem(new OpenReportCommand(_context.Store))
            {
                Title = "Aktuellen Report öffnen",
                Subtitle = $"Stand {m.Generated.ToLocalTime():g} · {DefenderModel.Fmt(m.Count)} Funde{(m.IsDemo ? " · Demo" : string.Empty)}",
                Icon = Glyphs.Report,
                MoreCommands = [new CommandContextItem(new TopActionsPage(_context)) { Title = "Top-Maßnahmen anzeigen" }],
            });
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or System.Text.Json.JsonException)
        {
            // no report yet
        }

        return [.. items];
    }
}

/// <summary>Runs the report and shows its log live.</summary>
internal sealed partial class ProgressPage : ContentPage
{
    private readonly DefenderContext _context;
    private readonly bool _demo;
    private readonly Action _done;
    private readonly MarkdownContent _content = new();
    private readonly StringBuilder _log = new();
    private string _heading = "⏳ Report wird aktualisiert …";
    private int _started;

    public ProgressPage(DefenderContext context, bool demo, Action done)
    {
        _context = context;
        _demo = demo;
        _done = done;
        Name = demo ? "Erzeugen" : "Aktualisieren";
        Title = demo ? "Demo-Report" : context.Settings.TenantName;
        Icon = Ui.Glyph("");
        Render();
    }

    public override IContent[] GetContent()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _ = Run();
        }

        return [_content];
    }

    private async Task Run()
    {
        IsLoading = true;
        try
        {
            var creds = _demo ? null : _context.Credentials(_context.Vault.Secret);
            var result = await Task.Run(() => DefenderReport.Generate(_context.Store, creds, _demo ? "Demo-Mandant" : _context.Settings.TenantName, _demo, Append));
            _heading = "✅ Report aktualisiert";
            Commands =
            [
                new CommandContextItem(new OpenReportCommand(_context.Store)) { Title = "Report öffnen" },
                new CommandContextItem(new TopActionsPage(_context)) { Title = "Top-Maßnahmen anzeigen" },
                Ui.Copy(_log.ToString(), "Protokoll kopieren"),
            ];
            Notify.Show("Report aktualisiert", $"{DefenderReport.Fmt(result.Findings)} Funde", new Uri(result.Path).AbsoluteUri);
        }
        catch (Exception ex)
        {
            Log.Error("defender report", ex);
            Append($"\n❌ {ex.Message}");
            _heading = "❌ Aktualisierung fehlgeschlagen";
            Commands = [Ui.Copy(_log.ToString(), "Protokoll kopieren")];
        }
        finally
        {
            IsLoading = false;
            Render();
            _done();
        }
    }

    private void Append(string line)
    {
        lock (_log)
        {
            _log.AppendLine(line);
        }

        Render();
    }

    private void Render()
    {
        string text;
        lock (_log)
        {
            text = _log.ToString();
        }

        var hint = _started == 1 && IsLoading ? "\n\nDas Ergebnis erscheint hier; die Palette darf dafür offen bleiben." : string.Empty;
        var demo = _demo ? "\n\n_Demomodus: Es werden Beispieldaten verwendet, keine Verbindung zu Defender._" : string.Empty;
        _content.Body = $"# {_heading}{hint}{demo}\n\n```\n{(text.Length > 0 ? text : "Starte …")}\n```";
    }
}

internal sealed partial class CredentialsPage : ListPage
{
    private readonly DefenderContext _context;

    public CredentialsPage(DefenderContext context)
    {
        _context = context;
        Id = "defender.setup-credentials";
        Name = "Öffnen";
        Title = "Defender-Zugangsdaten";
        Icon = Ui.Glyph("");
    }

    public override IListItem[] GetItems()
    {
        var settings = _context.Settings;
        var items = new List<IListItem>();
        if (!settings.Configured)
        {
            items.Add(new ListItem(Ui.OpenSettings(DefenderProvider.ProviderId)) { Title = "1. Tenant-ID und Client-ID eintragen", Subtitle = "Enter: Einstellungen öffnen (App-Registrierung mit Vulnerability.Read.All)", Icon = Ui.Glyph("") });
            return [.. items];
        }

        if (_context.Gate.Items(() => RaiseItemsChanged()) is { } gate)
        {
            items.AddRange(gate);
        }
        else
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = $"Zugangsdaten hinterlegt: {settings.TenantName}",
                Subtitle = $"Tenant {settings.TenantId} · Client {settings.ClientId} · entsperrt",
                Icon = Ui.Glyph(""),
                MoreCommands = _context.Gate.SecurityActions(() => RaiseItemsChanged()),
            });
            items.AddRange(_context.Gate.SecurityActions(() => RaiseItemsChanged()).OfType<CommandContextItem>().Select(a => new ListItem(a.Command!) { Title = a.Title, Icon = a.Icon }));
        }

        items.Add(new ListItem(Ui.OpenSettings(DefenderProvider.ProviderId)) { Title = "Einstellungen öffnen", Subtitle = "Mandant, Tenant-ID, Client-ID, API-Adresse, Report-Ordner, Demodaten", Icon = Ui.Glyph("") });
        return [.. items];
    }
}

internal sealed partial class OpenReportCommand : InvokableCommand
{
    private readonly DefenderStore _store;

    public OpenReportCommand(DefenderStore store)
    {
        _store = store;
        Id = "defender.open-report";
        Name = "Report öffnen";
        Icon = Glyphs.Report;
    }

    public override ICommandResult Invoke()
    {
        try
        {
            _store.Open();
            return CommandResult.Dismiss();
        }
        catch (Exception ex)
        {
            return CommandResult.ShowToast(new ToastArgs { Message = ex.Message, Result = CommandResult.KeepOpen() });
        }
    }
}
