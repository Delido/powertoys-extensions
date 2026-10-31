using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PowerToysExtensions.Extensions.Defender;

/// <summary>The data of a report as written by <see cref="DefenderReport"/>: head plus 6 ints per finding.</summary>
public sealed class DefenderData
{
    public const int Dev = 0, Sw = 1, Cve = 2, Ver = 3, Day = 4, New = 5;

    public DefenderData(ReportHead head, int[] findings)
    {
        Head = head;
        F = findings;
        foreach (var node in head.Ev.OfType<JsonArray>())
        {
            Evidence[EvKey((int)node[0]!, (int)node[1]!, (int)node[2]!)] = [.. (node[3] as JsonArray ?? []).Select(p => (int)p!)];
        }
    }

    public ReportHead Head { get; }

    public int[] F { get; }

    /// <summary>(device, software, version) → path indexes.</summary>
    public Dictionary<long, int[]> Evidence { get; } = [];

    public static long EvKey(int d, int s, int v) => ((long)d << 40) | ((long)s << 20) | (uint)v;

    public string[] Groups => [.. Head.Devices.Select(d => d.G).Distinct().Order(StringComparer.Create(CultureInfo.GetCultureInfo("de-DE"), true))];
}

public sealed class SoftwareAgg
{
    public int I { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public bool UpdateAvailable { get; init; }

    public int DevCount { get; set; }

    public int CveCount { get; set; }

    public int VersCount { get; set; }

    public int[] SevCves { get; } = new int[4];

    public int MaxE { get; set; }

    public double Score { get; set; }

    public int Recent { get; set; }
}

public sealed class CveAgg
{
    public required RawCve Cve { get; init; }

    public int I { get; init; }

    public int DevCount { get; set; }

    public List<int> Sws { get; } = [];

    public int Recent { get; set; }

    public double Score { get; set; }
}

public sealed class DeviceAgg
{
    public required RawDevice Device { get; init; }

    public int I { get; init; }

    public int[] Sev { get; } = new int[4];

    public int CveCount { get; set; }

    public int SwCount { get; set; }

    public int MaxE { get; set; }

    public double Score { get; set; }

    public int N { get; set; }

    public int Recent { get; set; }
}

/// <summary>Aggregates the findings like the HTML report (optionally for one device group).</summary>
public sealed partial class DefenderModel
{
    public static readonly string[] SevNames = ["Niedrig", "Mittel", "Hoch", "Kritisch"];
    public static readonly string[] ExpNames = ["Kein bekannter Exploit", "Exploit öffentlich", "Exploit verifiziert", "Exploit-Kit"];
    public static readonly string[] ExpShort = ["–", "Öffentlich", "Verifiziert", "Exploit-Kit"];

    private const int MaxRows = 25;
    private static readonly double[] WeightSev = [0.5, 2, 5, 10];
    private static readonly double[] WeightExp = [1, 1.5, 2.5, 3];

    private readonly int _today;
    private readonly bool _hasPrevious;
    private readonly int[] _offsets;
    private readonly Dictionary<int, SoftwareAgg> _swByIndex = [];

    public DefenderModel(DefenderData data, string? group = null)
    {
        Data = data;
        var f = data.F;
        var raw = data.Head;
        Generated = DateTimeOffset.TryParse(raw.Meta.Generated, CultureInfo.InvariantCulture, out var g) ? g : DateTimeOffset.Now;
        PrevRun = DateTimeOffset.TryParse(raw.Meta.PrevRun, CultureInfo.InvariantCulture, out var p) ? p : null;
        _hasPrevious = PrevRun is not null;
        _today = (int)Math.Floor(Generated.ToUnixTimeMilliseconds() / 86400000.0);

        // offsets in the filter, sorted by device: unique software/CVEs per device can then be counted
        // with small sets that are cleared per device
        var total = f.Length / ReportBuilder.Fields;
        var offsets = new List<int>(total);
        for (var i = 0; i < total; i++)
        {
            var o = i * ReportBuilder.Fields;
            if (group is null || raw.Devices[f[o + DefenderData.Dev]].G == group)
            {
                offsets.Add(o);
            }
        }

        offsets.Sort((a, b) => f[a + DefenderData.Dev] != f[b + DefenderData.Dev] ? f[a + DefenderData.Dev].CompareTo(f[b + DefenderData.Dev]) : a.CompareTo(b));
        _offsets = [.. offsets];

        var cves = new Dictionary<int, CveAgg>();
        var devices = new Dictionary<int, DeviceAgg>();
        var swCve = new HashSet<long>();
        var swVer = new HashSet<long>();
        var devSw = new HashSet<int>();
        var devCve = new HashSet<int>();
        var lastDev = -1;
        foreach (var o in _offsets)
        {
            int di = f[o + DefenderData.Dev], si = f[o + DefenderData.Sw], ci = f[o + DefenderData.Cve], vi = f[o + DefenderData.Ver];
            var c = raw.Cves[ci];
            var d = raw.Devices[di];
            var s = raw.Sw[si];
            var weight = WeightSev[c.S] * WeightExp[c.E];
            var fresh = IsNew(o);
            if (di != lastDev)
            {
                devSw.Clear();
                devCve.Clear();
                lastDev = di;
            }

            if (!_swByIndex.TryGetValue(si, out var a))
            {
                _swByIndex[si] = a = new SoftwareAgg { I = si, Name = SoftwareName(s.V, s.N), Key = $"{s.V}|{s.N}", Action = SoftwareAction(s), UpdateAvailable = s.U || s.R.Length > 0 };
            }

            if (devSw.Add(si))
            {
                a.DevCount++;
            }

            if (swCve.Add(((long)si << 32) | (uint)ci))
            {
                a.CveCount++;
                a.SevCves[c.S]++;
            }

            if (swVer.Add(((long)si << 32) | (uint)vi))
            {
                a.VersCount++;
            }

            a.Score += weight;
            a.MaxE = Math.Max(a.MaxE, c.E);
            if (fresh)
            {
                a.Recent++;
            }

            if (!cves.TryGetValue(ci, out var b))
            {
                cves[ci] = b = new CveAgg { I = ci, Cve = c };
            }

            if (devCve.Add(ci))
            {
                b.DevCount++;
            }

            if (!b.Sws.Contains(si))
            {
                b.Sws.Add(si);
            }

            if (fresh)
            {
                b.Recent++;
            }

            if (!devices.TryGetValue(di, out var x))
            {
                devices[di] = x = new DeviceAgg { I = di, Device = d };
            }

            x.Sev[c.S]++;
            x.CveCount = devCve.Count;
            x.SwCount = devSw.Count;
            x.MaxE = Math.Max(x.MaxE, c.E);
            x.Score += weight;
            x.N++;
            if (fresh)
            {
                x.Recent++;
            }
        }

        foreach (var b in cves.Values)
        {
            b.Score = b.DevCount * WeightSev[b.Cve.S] * WeightExp[b.Cve.E];
        }

        Software = [.. _swByIndex.Values.OrderByDescending(s => s.Score)];
        Cves = [.. cves.Values.OrderByDescending(c => c.Score)];
        Devices = [.. devices.Values.OrderByDescending(d => d.Score)];
    }

    public DefenderData Data { get; }

    public DateTimeOffset Generated { get; }

    public DateTimeOffset? PrevRun { get; }

    public SoftwareAgg[] Software { get; }

    public CveAgg[] Cves { get; }

    public DeviceAgg[] Devices { get; }

    public int Count => _offsets.Length;

    public string NewLabel => _hasPrevious ? "neu seit letztem Lauf" : "neu (7 Tage)";

    public bool IsDemo => Data.Head.Meta.Demo;

    public static string Fmt(double n) => DefenderReport.Fmt(n);

    public static string Compact(double n) => n >= 10000 ? $"{Fmt(n / 1000)} Tsd." : Fmt(n);

    public static string SoftwareName(string vendor, string name)
    {
        var v = Pretty(vendor);
        var n = Pretty(name);
        return n.StartsWith(v, StringComparison.OrdinalIgnoreCase) ? n : $"{v} {n}";
    }

    public static string CveUrl(string id) => $"https://security.microsoft.com/vulnerabilities/vulnerability/{Uri.EscapeDataString(id)}/overview";

    public static string DeviceUrl(string id) => $"https://security.microsoft.com/machines/v2/{Uri.EscapeDataString(id)}/overview";

    public static string NvdUrl(string id) => $"https://nvd.nist.gov/vuln/detail/{Uri.EscapeDataString(id)}";

    public bool IsNew(int o) => _hasPrevious ? Data.F[o + DefenderData.New] == 1 : _today - Data.F[o + DefenderData.Day] <= 7;

    public IEnumerable<int> Select(Func<int, bool> predicate) => _offsets.Where(predicate);

    public string[] PathsOf(int d, int s, int v) =>
        Data.Evidence.TryGetValue(DefenderData.EvKey(d, s, v), out var paths) ? [.. paths.Select(i => Data.Head.Paths[i])] : [];

    /// <summary>Devices with an installation path containing the text (path search).</summary>
    public HashSet<int> DevicesWithPath(string query)
    {
        var hits = new HashSet<int>();
        for (var i = 0; i < Data.Head.Paths.Count; i++)
        {
            if (Data.Head.Paths[i].Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(i);
            }
        }

        var devices = new HashSet<int>();
        if (hits.Count > 0)
        {
            foreach (var (key, paths) in Data.Evidence)
            {
                if (paths.Any(hits.Contains))
                {
                    devices.Add((int)(key >> 40));
                }
            }
        }

        return devices;
    }

    public string SoftwareNameOf(int si) => _swByIndex.TryGetValue(si, out var a) ? a.Name : Data.Head.Sw[si].N;

    public string SoftwareActionOf(int si) => _swByIndex.TryGetValue(si, out var a) ? a.Action : string.Empty;

    /// <summary>Names of the affected devices, one per line (for the copy actions).</summary>
    public string DeviceNames(Func<int, bool> predicate) =>
        string.Join('\n', Select(predicate).Select(o => Data.Head.Devices[Data.F[o + DefenderData.Dev]].N).Distinct().Order(StringComparer.Ordinal));

    public string DevicePaths(DeviceAgg d) =>
        string.Join('\n', Select(o => Data.F[o + DefenderData.Dev] == d.I).SelectMany(o => PathsOf(d.I, Data.F[o + DefenderData.Sw], Data.F[o + DefenderData.Ver])).Distinct());

    public string SoftwareMarkdown(SoftwareAgg a)
    {
        var f = Data.F;
        var byDev = new Dictionary<int, (int N, int V, int Crit, bool Fresh)>();
        var cveSet = new HashSet<int>();
        foreach (var o in Select(o => f[o + DefenderData.Sw] == a.I))
        {
            var di = f[o + DefenderData.Dev];
            (int N, int V, int Crit, bool Fresh) x = byDev.GetValueOrDefault(di, (0, f[o + DefenderData.Ver], 0, false));
            byDev[di] = (x.N + 1, x.V, x.Crit + (Data.Head.Cves[f[o + DefenderData.Cve]].S == 3 ? 1 : 0), x.Fresh || IsNew(o));
            cveSet.Add(f[o + DefenderData.Cve]);
        }

        var devices = byDev.OrderByDescending(p => p.Value.Crit).ThenByDescending(p => p.Value.N).ToArray();
        var cves = cveSet.Select(ci => Data.Head.Cves[ci]).OrderByDescending(c => c.S).ThenByDescending(c => c.E).ThenByDescending(c => c.C).ToArray();
        var md = new StringBuilder();
        md.Append(CultureInfo.InvariantCulture, $"## {a.Name}\n\n**Maßnahme:** {a.Action}{(a.UpdateAvailable ? string.Empty : " _(kein Update verfügbar)_")}\n\n### Verwundbare CVEs\n\n");
        foreach (var c in cves.Take(MaxRows))
        {
            md.Append(CultureInfo.InvariantCulture, $"- {SevNames[c.S]} · **{c.Id}** · CVSS {Cvss(c.C)}{(c.E > 0 ? $" · ⚠ {ExpNames[c.E]}" : string.Empty)}\n");
        }

        md.Append(More(cves.Length)).Append("\n### Betroffene Geräte und Fundstellen\n\n");
        foreach (var (di, x) in devices.Take(MaxRows))
        {
            var d = Data.Head.Devices[di];
            md.Append(CultureInfo.InvariantCulture, $"- **{d.N}** ({d.G}) · Version {Data.Head.Vers[x.V]} · {x.N} CVEs{NewBadge(x.Fresh)}\n{PathsMarkdown(PathsOf(di, a.I, x.V))}");
        }

        return md.Append(More(devices.Length)).ToString();
    }

    public string CveMarkdown(CveAgg b)
    {
        var f = Data.F;
        var c = b.Cve;
        var mine = Select(o => f[o + DefenderData.Cve] == b.I).ToArray();
        var md = new StringBuilder();
        md.Append(CultureInfo.InvariantCulture, $"## {c.Id}\n\n{SevNames[c.S]} · CVSS {Cvss(c.C)} · {ExpNames[c.E]}\n\n");
        if (c.Kb is { Length: > 0 } kb)
        {
            md.Append(CultureInfo.InvariantCulture, $"**Empfohlenes Sicherheitsupdate:** [KB{kb}]({(c.Kbu is { Length: > 0 } url ? url : $"https://catalog.update.microsoft.com/Search.aspx?q=KB{kb}")})\n\n");
        }

        md.Append("### Software\n\n");
        foreach (var si in b.Sws)
        {
            md.Append(CultureInfo.InvariantCulture, $"- **{SoftwareNameOf(si)}** → {SoftwareActionOf(si)}\n");
        }

        md.Append("\n### Betroffene Geräte und Fundstellen\n\n");
        foreach (var o in mine.Take(MaxRows))
        {
            var d = Data.Head.Devices[f[o + DefenderData.Dev]];
            md.Append(CultureInfo.InvariantCulture, $"- **{d.N}** ({d.G}) · Version {Data.Head.Vers[f[o + DefenderData.Ver]]}{NewBadge(IsNew(o))}\n{PathsMarkdown(PathsOf(f[o + DefenderData.Dev], f[o + DefenderData.Sw], f[o + DefenderData.Ver]))}");
        }

        return md.Append(More(mine.Length)).ToString();
    }

    public string DeviceMarkdown(DeviceAgg d)
    {
        var f = Data.F;
        var bySw = new Dictionary<int, (int N, int V, int Crit, int High, int Fresh)>();
        foreach (var o in Select(o => f[o + DefenderData.Dev] == d.I))
        {
            var si = f[o + DefenderData.Sw];
            (int N, int V, int Crit, int High, int Fresh) x = bySw.GetValueOrDefault(si, (0, f[o + DefenderData.Ver], 0, 0, 0));
            var s = Data.Head.Cves[f[o + DefenderData.Cve]].S;
            bySw[si] = (x.N + 1, x.V, x.Crit + (s == 3 ? 1 : 0), x.High + (s == 2 ? 1 : 0), x.Fresh + (IsNew(o) ? 1 : 0));
        }

        var rows = bySw.OrderByDescending(p => p.Value.Crit).ThenByDescending(p => p.Value.High).ThenByDescending(p => p.Value.N).ToArray();
        var device = d.Device;
        var md = new StringBuilder();
        md.Append(CultureInfo.InvariantCulture, $"## {device.N}\n\n{device.G} · {device.Os}{(device.T.Length > 0 ? $" · {string.Join(", ", device.T)}" : string.Empty)}\n\n### Zu aktualisierende Software\n\n");
        foreach (var (si, x) in rows.Take(MaxRows))
        {
            md.Append(CultureInfo.InvariantCulture, $"- **{SoftwareNameOf(si)}** {Data.Head.Vers[x.V]} · {x.N} CVEs ({x.Crit} krit., {x.High} hoch){(x.Fresh > 0 ? $" · 🆕 {x.Fresh}" : string.Empty)}\n  → {SoftwareActionOf(si)}\n{PathsMarkdown(PathsOf(d.I, si, x.V))}");
        }

        return md.Append(More(rows.Length)).ToString();
    }

    private static string Cvss(double c) => c > 0 ? c.ToString("0.0", CultureInfo.InvariantCulture) : "–";

    private static string NewBadge(bool yes) => yes ? " **🆕**" : string.Empty;

    private static string More(int total) => total > MaxRows ? $"\n_… und {Fmt(total - MaxRows)} weitere. Vollständige Liste im Report._\n" : string.Empty;

    private static string PathsMarkdown(string[] paths)
    {
        var md = new StringBuilder();
        foreach (var p in paths.Where(p => !p.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase)))
        {
            md.Append(CultureInfo.InvariantCulture, $"  - `{p}`\n");
        }

        foreach (var p in paths.Where(p => p.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase)))
        {
            md.Append(CultureInfo.InvariantCulture, $"  - `{p}` _(Registry)_\n");
        }

        return md.ToString();
    }

    private static string Pretty(string s) =>
        WordStart().Replace(s.Replace('_', ' '), m => m.Value.ToUpperInvariant());

    // Defender sometimes already names the target version in the text ("… to version 159.0.0.0")
    private static string SoftwareAction(RawSoftware s) =>
        s.R.Length > 0
            ? s.R + (s.Rv.Length > 0 && !s.R.Contains(s.Rv, StringComparison.Ordinal) ? $" → {s.Rv}" : string.Empty)
            : s.U ? "Update installieren" : "Kein Update verfügbar, Workaround/Ersatz prüfen";

    [GeneratedRegex(@"(?<=^|[\s-])[a-z]")]
    private static partial Regex WordStart();
}
