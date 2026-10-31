using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.Defender;

// Data of the HTML report (dictionary-encoded, field names as in the template and the Raycast version).
public sealed class RawDevice
{
    public string? Id { get; set; }

    public string N { get; set; } = string.Empty;

    public string G { get; set; } = string.Empty;

    public string Os { get; set; } = string.Empty;

    public string[] T { get; set; } = [];

    public string X { get; set; } = string.Empty;

    public string L { get; set; } = string.Empty;
}

public sealed class RawSoftware
{
    public string V { get; set; } = string.Empty;

    public string N { get; set; } = string.Empty;

    public string R { get; set; } = string.Empty;

    public string Rv { get; set; } = string.Empty;

    public bool U { get; set; }
}

public sealed class RawCve
{
    public string Id { get; set; } = string.Empty;

    public int S { get; set; }

    public double C { get; set; }

    public int E { get; set; }

    public string? Kb { get; set; }

    public string? Kbn { get; set; }

    public string? Kbu { get; set; }
}

public sealed class Snapshot
{
    public string D { get; set; } = string.Empty;

    public int T { get; set; }

    public int[] S { get; set; } = [0, 0, 0, 0];

    public int E { get; set; }

    public int Dv { get; set; }

    public int C { get; set; }
}

public sealed class ChangeDay
{
    public string D { get; set; } = string.Empty;

    public int[] N { get; set; } = [0, 0, 0, 0];

    public int[] X { get; set; } = [0, 0, 0, 0];
}

public sealed class ReportMeta
{
    public string Generated { get; set; } = string.Empty;

    public string? PrevRun { get; set; }

    public string Tenant { get; set; } = string.Empty;

    public bool Demo { get; set; }

    public Dictionary<string, bool> Sources { get; set; } = [];
}

/// <summary>Everything except the findings (aktuell.json); the findings are 6 Int32 values each (aktuell.findings.bin).</summary>
public sealed class ReportHead
{
    public ReportMeta Meta { get; set; } = new();

    public List<RawDevice> Devices { get; set; } = [];

    public List<RawSoftware> Sw { get; set; } = [];

    public List<RawCve> Cves { get; set; } = [];

    public List<string> Vers { get; set; } = [];

    public List<string> Paths { get; set; } = [];

    public List<Snapshot> History { get; set; } = [];

    public List<ChangeDay> Changes { get; set; } = [];

    /// <summary>[device, group, vendor, product, CVE, severity] of findings fixed since the last run.</summary>
    public JsonArray Fixed { get; set; } = [];

    /// <summary>[device, software, version, [path indexes]] per installation.</summary>
    public JsonArray Ev { get; set; } = [];
}

/// <summary>State of a run, compared with the next run (new / fixed since the last run).</summary>
public sealed class FindingState
{
    public int V { get; set; } = 2;

    public string Time { get; set; } = string.Empty;

    public List<string[]> Devices { get; set; } = [];

    public List<string[]> Sw { get; set; } = [];

    public List<string> Cves { get; set; } = [];

    /// <summary>Per finding: device, software, CVE, severity.</summary>
    public List<int> Items { get; set; } = [];
}

public sealed record DefenderCredentials(string TenantId, string ClientId, string ClientSecret, string ApiBase);

public sealed record GenerateResult(string Path, int Findings, int NewCount, int Fixed);

/// <summary>Fetches the Defender vulnerability data and writes the HTML report plus the data for the lists.</summary>
public static class DefenderReport
{
    public const string DefaultApiBase = "https://api.security.microsoft.com";
    private const int VulnPageSize = 5000;
    private const int ChangesPageSize = 10000;

    public static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // The export APIs answer slowly for large tenants.
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static readonly SemaphoreSlim Running = new(1, 1);

    public static bool IsRunning => Running.CurrentCount == 0;

    public static string Fmt(double n) => Math.Round(n).ToString("N0", German);

    public static async Task<string> Token(DefenderCredentials creds, CancellationToken token = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = creds.ClientId,
            ["client_secret"] = creds.ClientSecret,
            ["scope"] = "https://api.securitycenter.microsoft.com/.default",
            ["grant_type"] = "client_credentials",
        });
        using var response = await Client.PostAsync($"https://login.microsoftonline.com/{Uri.EscapeDataString(creds.TenantId)}/oauth2/v2.0/token", content, token);
        JsonNode? json = null;
        try
        {
            json = JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
        }
        catch (JsonException)
        {
            // no JSON
        }

        if (!response.IsSuccessStatusCode || Graph.Str(json?["access_token"]) is not { Length: > 0 } accessToken)
        {
            // the first line is enough, e.g. "AADSTS7000215: Invalid client secret provided."
            var message = (Graph.Str(json?["error_description"]) is { Length: > 0 } d ? d : Graph.Str(json?["error"]) is { Length: > 0 } e ? e : response.ReasonPhrase ?? string.Empty).Split('\n')[0].Trim();
            throw new InvalidOperationException($"Anmeldung fehlgeschlagen ({(int)response.StatusCode}): {message}");
        }

        return accessToken;
    }

    public static async Task<GenerateResult> Generate(DefenderStore store, DefenderCredentials? creds, string tenantName, bool demo, Action<string> log, CancellationToken token = default)
    {
        if (!await Running.WaitAsync(0, token))
        {
            throw new InvalidOperationException("Es läuft bereits eine Aktualisierung.");
        }

        try
        {
            return await Run(store, creds, tenantName, demo, log, token);
        }
        finally
        {
            Running.Release();
            store.Invalidate();
        }
    }

    private static async Task<GenerateResult> Run(DefenderStore store, DefenderCredentials? creds, string tenantName, bool demo, Action<string> log, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        string Seconds() => $"{Math.Round((DateTime.UtcNow - started).TotalSeconds)} s";
        Directory.CreateDirectory(store.ReportsDir);

        bool hadMachines = false, hadRecommendations = false;
        var changes = new ChangeSummary();
        var hadChanges = true;
        FindingState? previousState;
        ReportBuilder builder;
        if (demo)
        {
            log("Erzeuge Demodaten …");
            var data = DefenderDemo.Data();
            hadMachines = hadRecommendations = true;
            foreach (var change in data.Changes)
            {
                changes.Add(change);
            }

            previousState = data.Previous;
            builder = new ReportBuilder(data.Machines, data.Recommendations, new PreviousRun(previousState));
            foreach (var row in data.Rows)
            {
                builder.Add(row);
            }
        }
        else
        {
            if (creds is null)
            {
                throw new InvalidOperationException("Keine Zugangsdaten.");
            }

            var apiBase = (creds.ApiBase.Length > 0 ? creds.ApiBase : DefaultApiBase).TrimEnd('/');
            log("Melde an …");
            var accessToken = await Token(creds, token);
            previousState = store.ReadState();

            // devices and recommendations first, so that the findings can be linked while they are fetched
            log("Lade Geräte (Tags, Exposure) …");
            var machines = new List<JsonNode>();
            hadMachines = await FetchPages(apiBase, "/api/machines?$top=10000", accessToken, page => machines.AddRange(page.Select(SlimMachine)), log, token, optional: true);
            log("Lade Sicherheitsempfehlungen …");
            var recommendations = new List<JsonNode>();
            hadRecommendations = await FetchPages(apiBase, "/api/recommendations", accessToken, page => recommendations.AddRange(page.Select(SlimRecommendation)), log, token, optional: true);

            log("Lade Schwachstellen pro Gerät (Export-API) …");
            builder = new ReportBuilder(machines, recommendations, previousState is null ? null : new PreviousRun(previousState));
            await FetchPages(apiBase, $"/api/machines/SoftwareVulnerabilitiesByMachine?pageSize={VulnPageSize}", accessToken, page =>
            {
                foreach (var row in page)
                {
                    builder.Add(row);
                }
            }, log, token);

            log("Lade Änderungen der letzten 14 Tage …");
            var since = DateTime.UtcNow.AddDays(-14).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            hadChanges = await FetchPages(apiBase, $"/api/machines/SoftwareVulnerabilityChangesByMachine?pageSize={ChangesPageSize}&sinceTime={since}", accessToken, page =>
            {
                foreach (var change in page)
                {
                    changes.Add(change);
                }
            }, log, token, optional: true);
        }

        log($"Daten geladen in {Seconds()}");
        var generated = DateTime.Now;
        var snapshot = builder.Snapshot(generated);
        var fixedItems = builder.Previous?.Fixed(builder.CurrentKeys) ?? [];

        // history: one entry per day, the last run of the day wins; demo runs leave the real data alone
        List<Snapshot> history;
        if (demo)
        {
            history = DefenderDemo.History(snapshot);
        }
        else
        {
            history = [.. store.ReadHistory().Where(h => h.D != snapshot.D).Append(snapshot).OrderBy(h => h.D, StringComparer.Ordinal)];
            File.WriteAllText(store.HistoryPath, JsonSerializer.Serialize(history, Json));
            store.WriteState(builder.State(generated));
        }

        var head = new ReportHead
        {
            Meta = new ReportMeta
            {
                Generated = generated.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                PrevRun = previousState?.Time,
                Tenant = tenantName,
                Demo = demo,
                Sources = new() { ["machines"] = hadMachines, ["recommendations"] = hadRecommendations, ["changes"] = hadChanges },
            },
            Devices = builder.Devices,
            Sw = builder.Sw,
            Cves = builder.Cves,
            Vers = builder.Vers,
            Paths = builder.Paths,
            History = history,
            Changes = hadChanges ? changes.Result() : [],
            Fixed = new JsonArray([.. fixedItems.Select(f => (JsonNode)new JsonArray(f.Device, f.Group, f.Vendor, f.Product, f.Cve, f.Severity))]),
            Ev = builder.EvidenceArray(),
        };

        // the default encoder escapes "<", so nothing in the data can end the <script> element of the template
        var headJson = JsonSerializer.Serialize(head, Json);
        var template = await File.ReadAllTextAsync(Paths.Asset(Path.Combine("defender", "report-template.html")), token);
        var marker = template.IndexOf("__REPORT_DATA__", StringComparison.Ordinal);
        var dated = Path.Combine(store.ReportsDir, $"Defender-Schwachstellen_{generated.ToUniversalTime():yyyyMMdd_HHmm}.html");
        await using (var writer = new StreamWriter(dated, false, new UTF8Encoding(false)))
        {
            await writer.WriteAsync(template.AsMemory(0, marker), token);
            await writer.WriteAsync(headJson.AsMemory(0, headJson.Length - 1), token);
            await writer.WriteAsync(",\"f\":");
            builder.WriteFindingsJson(writer);
            await writer.WriteAsync('}');
            await writer.WriteAsync(template.AsMemory(marker + "__REPORT_DATA__".Length), token);
        }

        File.Copy(dated, store.HtmlPath, overwrite: true);
        await File.WriteAllBytesAsync(store.FindingsPath, builder.FindingsBytes(), token);
        await File.WriteAllTextAsync(store.JsonPath, headJson, token);

        log(string.Empty);
        log($"Fertig in {Seconds()}: {Fmt(builder.Count)} Funde, {snapshot.C} CVEs, {snapshot.Dv} Geräte (kritisch: {snapshot.S[3]}, hoch: {snapshot.S[2]})");
        if (previousState is not null)
        {
            log($"Seit letztem Lauf: {builder.NewCount} neu, {fixedItems.Count} behoben");
        }

        log($"Report: {dated}");
        return new GenerateResult(dated, builder.Count, builder.NewCount, fixedItems.Count);
    }

    /// <summary>Fetches all pages of an endpoint; with optional=true an error is only logged (returns false).</summary>
    private static async Task<bool> FetchPages(string apiBase, string path, string accessToken, Action<JsonArray> onPage, Action<string> log, CancellationToken token, bool optional = false)
    {
        string? url = apiBase + path;
        var total = 0;
        while (url is not null)
        {
            HttpResponseMessage response;
            for (var attempt = 1; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                response = await Client.SendAsync(request, token);
                if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt <= 5)
                {
                    var wait = response.Headers.RetryAfter?.Delta is { } delta ? (int)delta.TotalSeconds : 30 * attempt;
                    response.Dispose();
                    log($"  API meldet {(int)response.StatusCode}, warte {wait} s (Versuch {attempt}/5) …");
                    await Task.Delay(TimeSpan.FromSeconds(wait), token);
                    continue;
                }

                break;
            }

            using (response)
            {
                JsonNode? json = null;
                try
                {
                    json = JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
                }
                catch (JsonException)
                {
                    // no JSON
                }

                if (!response.IsSuccessStatusCode)
                {
                    var message = $"{(int)response.StatusCode}: {(Graph.Str(json?["error"]?["message"]) is { Length: > 0 } m ? m : response.ReasonPhrase)}";
                    if (optional)
                    {
                        log($"  ⚠ übersprungen ({message})");
                        return false;
                    }

                    throw new InvalidOperationException($"API-Fehler bei {path.Split('?')[0]} – {message}");
                }

                var items = json?["value"] as JsonArray ?? [];
                total += items.Count;
                onPage(items);
                url = Graph.Str(json?["@odata.nextLink"]) is { Length: > 0 } next ? next : null;
                if (url is not null)
                {
                    log($"  … {Fmt(total)} Einträge");
                }
            }
        }

        log($"  {Fmt(total)} Einträge");
        return true;
    }

    // only the fields the report needs; the API returns a lot more per device
    private static JsonNode SlimMachine(JsonNode? m) => new JsonObject
    {
        ["id"] = Graph.Str(m?["id"]),
        ["computerDnsName"] = Graph.Str(m?["computerDnsName"]),
        ["rbacGroupName"] = Graph.Str(m?["rbacGroupName"]),
        ["osPlatform"] = Graph.Str(m?["osPlatform"]),
        ["machineTags"] = m?["machineTags"]?.DeepClone(),
        ["exposureLevel"] = Graph.Str(m?["exposureLevel"]),
        ["lastSeen"] = Graph.Str(m?["lastSeen"]),
    };

    private static JsonNode SlimRecommendation(JsonNode? r) => new JsonObject
    {
        ["id"] = Graph.Str(r?["id"]),
        ["recommendationName"] = Graph.Str(r?["recommendationName"]),
        ["recommendedVersion"] = Graph.Str(r?["recommendedVersion"]),
    };
}

/// <summary>Where reports, the comparison state and the history live.</summary>
public sealed class DefenderStore(Func<string> reportFolder)
{
    private readonly Lock _gate = new();
    private (DateTime Stamp, DefenderData Data)? _cache;

    public string ReportsDir => reportFolder() is { Length: > 0 } folder ? folder : Path.Combine(Paths.For("defender"), "reports");

    public string DataDir => Directory.CreateDirectory(Path.Combine(Paths.For("defender"), "data")).FullName;

    public string JsonPath => Path.Combine(ReportsDir, "aktuell.json");

    public string FindingsPath => Path.Combine(ReportsDir, "aktuell.findings.bin");

    public string HtmlPath => Path.Combine(ReportsDir, "aktuell.html");

    public string HistoryPath => Path.Combine(DataDir, "history.json");

    private string StatePath => Path.Combine(DataDir, "findings-state.v2.json.gz");

    /// <summary>The data of the current report (cached until a new report is written).</summary>
    public DefenderData Load()
    {
        lock (_gate)
        {
            if (!File.Exists(JsonPath) || !File.Exists(FindingsPath))
            {
                throw new FileNotFoundException($"Keine Daten gefunden in {ReportsDir}. Zuerst „Schwachstellen-Report aktualisieren“ ausführen.");
            }

            var stamp = File.GetLastWriteTimeUtc(FindingsPath);
            if (_cache is { } cached && cached.Stamp == stamp)
            {
                return cached.Data;
            }

            var head = JsonSerializer.Deserialize<ReportHead>(File.ReadAllText(JsonPath).TrimStart('﻿'), DefenderReport.Json) ?? throw new InvalidDataException("aktuell.json is empty");
            var bytes = File.ReadAllBytes(FindingsPath);
            var findings = new int[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, findings, 0, findings.Length * 4);
            var data = new DefenderData(head, findings);
            _cache = (stamp, data);
            return data;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _cache = null;
        }
    }

    public FindingState? ReadState()
    {
        try
        {
            using var file = File.OpenRead(StatePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<FindingState>(gzip, DefenderReport.Json) is { V: 2 } state ? state : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    public void WriteState(FindingState state)
    {
        using var file = File.Create(StatePath);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        JsonSerializer.Serialize(gzip, state, DefenderReport.Json);
    }

    public List<Snapshot> ReadHistory()
    {
        try
        {
            return JsonSerializer.Deserialize<List<Snapshot>>(File.ReadAllText(HistoryPath), DefenderReport.Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Opens the report, optionally at an anchor. Windows drops the #anchor when opening file:// links,
    /// so a small redirect page is written and opened instead.
    /// </summary>
    public void Open(string? hash = null)
    {
        if (!File.Exists(HtmlPath))
        {
            throw new FileNotFoundException($"{HtmlPath} fehlt. Zuerst „Schwachstellen-Report aktualisieren“ ausführen.");
        }

        if (hash is null)
        {
            Proc.Open(HtmlPath);
            return;
        }

        var redirect = Path.Combine(ReportsDir, "_open.html");
        var target = WebUtility.HtmlEncode($"aktuell.html#{hash}");
        File.WriteAllText(redirect, $"<!doctype html><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url={target}\"><title>Weiterleitung</title><a href=\"{target}\">Report öffnen</a>", Encoding.UTF8);
        Proc.Open(redirect);
    }
}

/// <summary>Builds the compact model of the report from the raw Defender rows (one finding = 6 ints).</summary>
internal sealed class ReportBuilder
{
    public const int Fields = 6;

    private static readonly Dictionary<string, int> SevCodes = new() { ["Critical"] = 3, ["High"] = 2, ["Medium"] = 1, ["Low"] = 0 };

    private readonly List<int> _f = [];
    private readonly List<int> _stateItems = [];
    private readonly HashSet<long> _otherVersions = [];
    private readonly Dictionary<string, int> _devIndex = [];
    private readonly Dictionary<string, int> _swIndex = [];
    private readonly Dictionary<string, int> _cveIndex = [];
    private readonly Dictionary<string, int> _verIndex = [];
    private readonly Dictionary<string, int> _pathIndex = [];
    private readonly Dictionary<long, int> _evIndex = [];
    private readonly List<int[]> _evPaths = [];
    private readonly List<(int D, int S, int V)> _evKeys = [];
    private readonly Dictionary<string, int> _dayCache = [];
    private readonly List<int> _findingsPerCve = [];
    private readonly Dictionary<string, JsonNode> _machines = [];
    private readonly Dictionary<string, JsonNode> _recs = [];

    public ReportBuilder(IEnumerable<JsonNode> machines, IEnumerable<JsonNode> recommendations, PreviousRun? previous)
    {
        foreach (var m in machines)
        {
            _machines[Graph.Str(m["id"])] = m;
        }

        foreach (var r in recommendations)
        {
            _recs[Graph.Str(r["id"])] = r;
        }

        Previous = previous;
    }

    public PreviousRun? Previous { get; }

    public List<RawDevice> Devices { get; } = [];

    public List<RawSoftware> Sw { get; } = [];

    public List<RawCve> Cves { get; } = [];

    public List<string> Vers { get; } = [];

    public List<string> Paths { get; } = [];

    public int Count { get; private set; }

    public int NewCount { get; private set; }

    /// <summary>Run key (device|CVE|product) → first installation; also detects duplicate rows.</summary>
    public Dictionary<long, int> CurrentKeys { get; } = [];

    public static int SevCode(JsonNode? value) => SevCodes.GetValueOrDefault(Text(value));

    public static int ExploitCode(JsonNode? value)
    {
        var s = Text(value);
        return s.Contains("InKit", StringComparison.Ordinal) || s.Contains("ZeroDay", StringComparison.Ordinal) ? 3
            : s.Contains("Verified", StringComparison.Ordinal) ? 2
            : s.Contains("Public", StringComparison.Ordinal) ? 1
            : 0;
    }

    /// <summary>Any JSON value as text (the API sometimes sends numbers where strings are expected).</summary>
    public static string Text(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => string.Empty,
    };

    /// <summary>64-bit FNV-1a: stable key across runs (indexes change, IDs don't).</summary>
    public static long RunKey(string deviceId, string cveId, string vendor, string product)
    {
        var hash = 0xcbf29ce484222325UL;
        foreach (var c in $"{deviceId}|{cveId}|{vendor}|{product}")
        {
            hash = (hash ^ c) * 0x100000001b3UL;
        }

        return (long)hash;
    }

    public void Add(JsonNode? row)
    {
        var cveId = Text(row?["cveId"]);
        if (row is null || cveId.Length == 0)
        {
            return;
        }

        var deviceId = Text(row["deviceId"]);
        var vendor = Text(row["softwareVendor"]);
        var product = Text(row["softwareName"]);

        if (!_devIndex.TryGetValue(deviceId, out var d))
        {
            var m = _machines.GetValueOrDefault(deviceId);
            d = Devices.Count;
            _devIndex[deviceId] = d;
            Devices.Add(new RawDevice
            {
                Id = deviceId,
                N = First(Text(m?["computerDnsName"]), Text(row["deviceName"]), deviceId),
                G = First(Text(row["rbacGroupName"]), Text(m?["rbacGroupName"]), "Ohne Gruppe"),
                Os = First(Text(row["osPlatform"]), Text(m?["osPlatform"]), "Unbekannt"),
                T = [.. (m?["machineTags"] as JsonArray ?? []).Select(Text).Where(t => t.Length > 0)],
                X = Text(m?["exposureLevel"]),
                L = Text(m?["lastSeen"]) is { Length: >= 10 } seen ? seen[..10] : string.Empty,
            });
        }

        var swKey = $"{vendor}|{product}";
        if (!_swIndex.TryGetValue(swKey, out var s))
        {
            s = Sw.Count;
            _swIndex[swKey] = s;
            Sw.Add(new RawSoftware { V = vendor, N = product });
        }

        var sw = Sw[s];
        if (sw.R.Length == 0)
        {
            if (_recs.GetValueOrDefault(Text(row["recommendationReference"])) is { } rec)
            {
                sw.R = Text(rec["recommendationName"]);
                sw.Rv = Text(rec["recommendedVersion"]);
            }
            else if (Text(row["recommendedSecurityUpdate"]) is { Length: > 0 } update)
            {
                sw.R = update;
            }
        }

        if (row["securityUpdateAvailable"] is JsonValue available && available.TryGetValue<bool>(out var yes) && yes)
        {
            sw.U = true;
        }

        var sev = SevCode(row["vulnerabilitySeverityLevel"]);
        var exp = ExploitCode(row["exploitabilityLevel"]);
        var cvss = double.TryParse(Text(row["cvssScore"]), NumberStyles.Float, CultureInfo.InvariantCulture, out var score) ? score : 0;
        RawCve entry;
        if (!_cveIndex.TryGetValue(cveId, out var c))
        {
            c = Cves.Count;
            _cveIndex[cveId] = c;
            entry = new RawCve { Id = cveId, S = sev, C = cvss, E = exp };
            Cves.Add(entry);
            _findingsPerCve.Add(0);
        }
        else
        {
            entry = Cves[c];
            entry.S = Math.Max(entry.S, sev);
            entry.E = Math.Max(entry.E, exp);
            entry.C = Math.Max(entry.C, cvss);
        }

        if (entry.Kb is null && Text(row["recommendedSecurityUpdateId"]) is { Length: > 0 } kb && kb != "null")
        {
            entry.Kb = kb;
            entry.Kbn = Text(row["recommendedSecurityUpdate"]);
            entry.Kbu = Text(row["recommendedSecurityUpdateUrl"]);
        }

        var version = Text(row["softwareVersion"]);
        if (!_verIndex.TryGetValue(version, out var v))
        {
            v = Vers.Count;
            _verIndex[version] = v;
            Vers.Add(version);
        }

        // the paths are the same for all CVEs of the same installation
        var evKey = ((long)d << 40) | ((long)s << 20) | (uint)v;
        if (!_evIndex.TryGetValue(evKey, out var e))
        {
            e = _evKeys.Count;
            _evIndex[evKey] = e;
            _evKeys.Add((d, s, v));
            var set = new SortedSet<int>();
            foreach (var path in (row["diskPaths"] as JsonArray ?? []).Concat(row["registryPaths"] as JsonArray ?? []).Select(Text).Where(p => p.Length > 0))
            {
                if (!_pathIndex.TryGetValue(path, out var pi))
                {
                    pi = Paths.Count;
                    _pathIndex[path] = pi;
                    Paths.Add(path);
                }

                set.Add(pi);
            }

            _evPaths.Add([.. set]);
        }

        var key = RunKey(deviceId, cveId, vendor, product);
        if (!CurrentKeys.TryGetValue(key, out var firstInstall))
        {
            CurrentKeys[key] = e;
            _stateItems.AddRange([d, s, c, sev]);
        }
        else if (firstInstall == e)
        {
            return; // duplicate row
        }
        else if (!_otherVersions.Add(((long)e << 24) | (uint)c))
        {
            return; // rare: the same CVE in several versions of one product
        }

        var date = Text(row["firstSeenTimestamp"]) is { Length: >= 10 } seenAt ? seenAt[..10] : string.Empty;
        if (!_dayCache.TryGetValue(date, out var day))
        {
            day = DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? (int)(parsed - DateTime.UnixEpoch).TotalDays
                : 0;
            _dayCache[date] = day;
        }

        var isNew = Previous is not null && !Previous.Keys.Contains(key) ? 1 : 0;
        _f.AddRange([d, s, c, v, day, isNew]);
        Count++;
        NewCount += isNew;
        _findingsPerCve[c]++;
    }

    public JsonArray EvidenceArray() =>
        new([.. _evKeys.Select((k, i) => (k, i)).Where(x => _evPaths[x.i].Length > 0)
            .Select(x => (JsonNode)new JsonArray(x.k.D, x.k.S, x.k.V, new JsonArray([.. _evPaths[x.i].Select(p => (JsonNode)p)])))]);

    public void WriteFindingsJson(TextWriter writer)
    {
        writer.Write('[');
        var line = new StringBuilder();
        for (var i = 0; i < Count; i++)
        {
            var o = i * Fields;
            line.Clear();
            if (i > 0)
            {
                line.Append(',');
            }

            line.Append('[').Append(_f[o]).Append(',').Append(_f[o + 1]).Append(',').Append(_f[o + 2]).Append(',')
                .Append(_f[o + 3]).Append(',').Append(_f[o + 4]).Append(',').Append(_f[o + 5]).Append(']');
            writer.Write(line);
        }

        writer.Write(']');
    }

    /// <summary>The findings as little-endian Int32 values.</summary>
    public byte[] FindingsBytes()
    {
        var bytes = new byte[_f.Count * 4];
        Buffer.BlockCopy(_f.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public FindingState State(DateTime time) => new()
    {
        Time = time.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        Devices = [.. Devices.Select(d => new[] { d.Id ?? string.Empty, d.N, d.G })],
        Sw = [.. Sw.Select(s => new[] { s.V, s.N })],
        Cves = [.. Cves.Select(c => c.Id)],
        Items = _stateItems,
    };

    public Snapshot Snapshot(DateTime date)
    {
        var s = new int[4];
        var e = 0;
        for (var i = 0; i < Cves.Count; i++)
        {
            s[Cves[i].S] += _findingsPerCve[i];
            if (Cves[i].E > 0)
            {
                e += _findingsPerCve[i];
            }
        }

        return new Snapshot { D = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), T = Count, S = s, E = e, Dv = Devices.Count, C = Cves.Count };
    }

    private static string First(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? string.Empty;
}

internal sealed record FixedFinding(string Device, string Group, string Vendor, string Product, string Cve, int Severity);

/// <summary>The findings of the last run, to mark new findings and list fixed ones.</summary>
internal sealed class PreviousRun
{
    private readonly FindingState _state;

    public PreviousRun(FindingState state)
    {
        _state = state;
        for (var i = 0; i + 3 < state.Items.Count; i += 4)
        {
            Keys.Add(KeyAt(i));
        }
    }

    public HashSet<long> Keys { get; } = [];

    /// <summary>Findings that were there last time and are missing now.</summary>
    public List<FixedFinding> Fixed(Dictionary<long, int> current)
    {
        var result = new List<FixedFinding>();
        var items = _state.Items;
        for (var i = 0; i + 3 < items.Count; i += 4)
        {
            if (current.ContainsKey(KeyAt(i)))
            {
                continue;
            }

            var d = _state.Devices[items[i]];
            var s = _state.Sw[items[i + 1]];
            result.Add(new FixedFinding(d[1], d[2], s[0], s[1], _state.Cves[items[i + 2]], items[i + 3]));
        }

        return result;
    }

    private long KeyAt(int i)
    {
        var d = _state.Devices[_state.Items[i]];
        var s = _state.Sw[_state.Items[i + 1]];
        return ReportBuilder.RunKey(d[0], _state.Cves[_state.Items[i + 2]], s[0], s[1]);
    }
}

/// <summary>Counts change events (New/Fixed) per day and severity.</summary>
internal sealed class ChangeSummary
{
    private readonly Dictionary<string, ChangeDay> _days = [];

    public void Add(JsonNode? change)
    {
        var status = ReportBuilder.Text(change?["status"]);
        if (status is not ("New" or "Fixed") || !DateTimeOffset.TryParse(ReportBuilder.Text(change?["eventTimestamp"]), CultureInfo.InvariantCulture, out var time))
        {
            return;
        }

        var day = time.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!_days.TryGetValue(day, out var entry))
        {
            _days[day] = entry = new ChangeDay { D = day };
        }

        (status == "New" ? entry.N : entry.X)[ReportBuilder.SevCode(change?["vulnerabilitySeverityLevel"])]++;
    }

    public List<ChangeDay> Result() => [.. _days.Values.OrderBy(d => d.D, StringComparer.Ordinal)];
}
