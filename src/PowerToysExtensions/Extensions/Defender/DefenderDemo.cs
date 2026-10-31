using System.Globalization;
using System.Text.Json.Nodes;

namespace PowerToysExtensions.Extensions.Defender;

/// <summary>Realistic sample data in the format of the Defender API (for tests without access).</summary>
internal static class DefenderDemo
{
    private static readonly string[] Sevs = ["Low", "Medium", "High", "Critical"];

    private static readonly Product[] Catalog =
    [
        new("microsoft", "windows_11", "Update Microsoft Windows 11 (OS and built-in applications)", ["10.0.26100.6899", "10.0.26100.6584", "10.0.26100.4946"], 70, Os: "Windows11"),
        new("microsoft", "windows_10", "Update Microsoft Windows 10 (OS and built-in applications)", ["10.0.19045.6456", "10.0.19045.6332"], 55, Os: "Windows10"),
        new("microsoft", "windows_server_2022", "Update Microsoft Windows Server 2022 (OS and built-in applications)", ["10.0.20348.4294", "10.0.20348.4052", "10.0.20348.3807"], 60, Os: "WindowsServer2022"),
        new("microsoft", "windows_server_2019", "Update Microsoft Windows Server 2019 (OS and built-in applications)", ["10.0.17763.7919", "10.0.17763.7792"], 50, Os: "WindowsServer2019"),
        new("google", "chrome", "Update Google Chrome", ["141.0.7390.123", "140.0.7339.208", "138.0.7204.184", "135.0.7049.115"], 45, "142.0.7444.60", Pc: 0.85, Ps: 0.15, Path: @"C:\Program Files\Google\Chrome\Application\chrome.exe", UserPath: @"C:\Users\<user>\AppData\Local\Google\Chrome\Application\chrome.exe"),
        new("microsoft", "edge_chromium-based", "Update Microsoft Edge Chromium-based", ["141.0.3537.99", "140.0.3485.94"], 30, "142.0.3595.53", Pc: 1, Ps: 0.6, Path: @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"),
        new("mozilla", "firefox", "Update Mozilla Firefox", ["144.0", "143.0.4", "140.3.1"], 25, "144.0.2", Pc: 0.35, Ps: 0.05, Path: @"C:\Program Files\Mozilla Firefox\firefox.exe", UserPath: @"C:\Users\<user>\AppData\Local\Mozilla Firefox\firefox.exe"),
        new("microsoft", "office", "Update Microsoft Office", ["16.0.19231.20156", "16.0.19127.20264"], 20, Pc: 0.95, Ps: 0.05, Path: @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE"),
        new("adobe", "acrobat_reader_dc", "Update Adobe Acrobat Reader DC", ["25.001.20693", "24.005.20421"], 18, "25.001.20844", Pc: 0.6, Ps: 0.1, Path: @"C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe"),
        new("7-zip", "7-zip", "Update 7-zip 7-zip", ["24.09", "23.01", "19.00"], 6, "25.01", Pc: 0.4, Ps: 0.35, Path: @"C:\Program Files\7-Zip\7z.exe"),
        new("notepad-plus-plus", "notepad++", "Update Notepad-plus-plus Notepad++", ["8.8.1", "8.6.9"], 5, "8.8.7", Pc: 0.3, Ps: 0.3),
        new("oracle", "jre", "Update Oracle Jre", ["8.0.461", "8.0.401", "8.0.351"], 14, "8.0.471", Pc: 0.12, Ps: 0.3, Path: @"C:\Program Files\Java\jre1.8.0_401\bin\java.exe"),
        new("zoom", "zoom", "Update Zoom Zoom", ["6.5.12", "6.3.5"], 8, "6.6.6", Pc: 0.3, Ps: 0, Path: @"C:\Program Files\Zoom\bin\Zoom.exe", UserPath: @"C:\Users\<user>\AppData\Roaming\Zoom\bin\Zoom.exe"),
        new("videolan", "vlc_media_player", "Update Videolan Vlc Media Player", ["3.0.20", "3.0.18"], 4, "3.0.21", Pc: 0.2, Ps: 0),
        new("rarlab", "winrar", "Update Rarlab Winrar", ["7.11", "6.24"], 4, "7.13", Pc: 0.1, Ps: 0.05),
        new("simon_tatham", "putty", "Update Simon Tatham Putty", ["0.81", "0.78"], 3, "0.83", Pc: 0.15, Ps: 0.4),
        new("openssl", "openssl", "Update Openssl Openssl", ["3.0.15", "1.1.1w"], 9, "3.5.4", Pc: 0.08, Ps: 0.5, Path: @"C:\Program Files\Git\mingw64\bin\libcrypto-3-x64.dll"),
        new("python", "python", "Update Python Python", ["3.12.7", "3.10.11"], 7, "3.13.9", Pc: 0.15, Ps: 0.25, Path: @"C:\Program Files\Python312\python.exe", UserPath: @"C:\Users\<user>\AppData\Local\Programs\Python\Python312\python.exe"),
        new("nodejs", "node.js", "Update Nodejs Node.js", ["20.18.0", "18.20.4"], 9, "22.21.0", Pc: 0.12, Ps: 0.2),
        new("microsoft", ".net", "Update Microsoft .net", ["8.0.19", "6.0.36"], 8, "8.0.21", Pc: 0.7, Ps: 0.9, Path: @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.19\coreclr.dll"),
        new("apache", "tomcat", "Update Apache Tomcat", ["9.0.85", "8.5.100"], 12, "10.1.48", Pc: 0, Ps: 0.15, EndOfSupport: true, Path: @"D:\Apps\tomcat\lib\catalina.jar"),
    ];

    private static readonly (string Group, string Prefix, int Count, bool Server)[] Groups =
    [
        ("Clients Vertrieb", "PC-VT", 85, false),
        ("Clients Entwicklung", "PC-DEV", 60, false),
        ("Clients Verwaltung", "PC-VW", 70, false),
        ("Server Produktion", "SRV-PRD", 28, true),
        ("Server Test", "SRV-TST", 14, true),
    ];

    public static (List<JsonNode> Rows, List<JsonNode> Machines, List<JsonNode> Recommendations, List<JsonNode> Changes, FindingState Previous) Data()
    {
        var rnd = Rng(4711);
        int Int(int min, int max) => min + (int)Math.Floor(rnd() * (max - min));
        var now = DateTime.UtcNow;
        var used = new HashSet<string>();
        var cveLists = Catalog.Select(sw => Enumerable.Range(0, sw.Cves).Select(_ =>
        {
            string id;
            do
            {
                id = $"CVE-{2024 + Int(0, 3)}-{Int(20000, 64000)}";
            }
            while (!used.Add(id));

            var p = rnd();
            var sev = p < 0.1 ? "Critical" : p < 0.5 ? "High" : p < 0.9 ? "Medium" : "Low";
            var q = rnd() * (sev == "Critical" ? 0.6 : 1);
            var exp = q < 0.04 ? "ExploitIsInKit" : q < 0.11 ? "ExploitIsVerified" : q < 0.26 ? "ExploitIsPublic" : "NoExploit";
            var cvss = sev switch
            {
                "Critical" => 9 + (Int(0, 9) / 10.0),
                "High" => 7 + (Int(0, 19) / 10.0),
                "Medium" => 4 + (Int(0, 29) / 10.0),
                _ => 1 + (Int(0, 29) / 10.0),
            };
            return (Id: id, Sev: sev, Exp: exp, Cvss: cvss, K: Int(0, sw.Versions.Length));
        }).ToArray()).ToArray();

        var machines = new List<JsonNode>();
        var rows = new List<JsonNode>();

        // a simulated run from yesterday, so that "new / fixed since the last run" shows something
        var previous = new FindingState
        {
            Time = now.AddDays(-1).ToString("o", CultureInfo.InvariantCulture),
            Sw = [.. Catalog.Select(p => new[] { p.Vendor, p.Name })],
        };
        var prevCve = new Dictionary<string, int>();
        void Remember(int dev, int si, string cveId, string sev)
        {
            if (!prevCve.TryGetValue(cveId, out var ci))
            {
                ci = previous.Cves.Count;
                prevCve[cveId] = ci;
                previous.Cves.Add(cveId);
            }

            previous.Items.AddRange([dev, si, ci, Array.IndexOf(Sevs, sev)]);
        }

        foreach (var group in Groups)
        {
            for (var i = 1; i <= group.Count; i++)
            {
                var id = string.Concat(Enumerable.Range(0, 40).Select(_ => "0123456789abcdef"[Int(0, 16)]));
                var os = group.Server ? (rnd() < 0.65 ? "WindowsServer2022" : "WindowsServer2019") : (rnd() < 0.8 ? "Windows11" : "Windows10");
                string[] pool = group.Server ? ["Internet-facing", "Datenbank", "Dateiserver", "Web"] : ["Laptop", "Desktop", "Homeoffice", "VIP"];
                var name = $"{group.Prefix}-{i:000}";
                var dns = $"{name}.corp.local".ToLowerInvariant();
                previous.Devices.Add([id, dns, group.Group]);
                var prevDev = previous.Devices.Count - 1;
                machines.Add(new JsonObject
                {
                    ["id"] = id,
                    ["computerDnsName"] = dns,
                    ["osPlatform"] = os,
                    ["rbacGroupName"] = group.Group,
                    ["machineTags"] = new JsonArray([.. pool.Where(_ => rnd() < 0.25).Select(t => (JsonNode)t)]),
                    ["exposureLevel"] = new[] { "Low", "Medium", "High" }[Int(0, 3)],
                    ["lastSeen"] = now.AddHours(-Int(1, 200)).ToString("o", CultureInfo.InvariantCulture),
                });

                var diligence = rnd();
                for (var si = 0; si < Catalog.Length; si++)
                {
                    var sw = Catalog[si];
                    var installed = sw.Os is not null ? sw.Os == os : rnd() < (group.Server ? sw.Ps : sw.Pc);
                    if (!installed || (rnd() < 0.25 + (0.4 * diligence) && !sw.EndOfSupport))
                    {
                        continue;
                    }

                    var vi = Math.Min(sw.Versions.Length - 1, (int)Math.Floor(Math.Pow(rnd(), 1 + (2 * diligence)) * sw.Versions.Length));
                    var baseAge = Int(3, 120);
                    var disk = new List<string>();
                    var reg = new List<string>();
                    int? kb = null;
                    if (sw.Os is not null)
                    {
                        kb = 5066000 + (sw.Versions.Length * 100) + vi;
                    }
                    else
                    {
                        var label = TitleCase(sw.Name);
                        disk.Add(sw.Path ?? $@"C:\Program Files\{label}\{sw.Name.Replace("_", string.Empty, StringComparison.Ordinal)}.exe");
                        reg.Add($@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{label}");
                        if (sw.UserPath is not null && !group.Server && rnd() < 0.5)
                        {
                            disk.Add(sw.UserPath.Replace("<user>", $"u{Int(1000, 9999)}", StringComparison.Ordinal));
                            reg.Add($@"HKEY_USERS\S-1-5-21-1111111111-2222222222-3333333333-{Int(1100, 9999)}\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{label}");
                        }
                    }

                    foreach (var cve in cveLists[si])
                    {
                        if (cve.K > vi)
                        {
                            // vulnerable yesterday, updated since
                            if (cve.K == vi + 1 && rnd() < 0.12)
                            {
                                Remember(prevDev, si, cve.Id, cve.Sev);
                            }

                            continue;
                        }

                        var age = cve.K == 0 ? Int(0, 14) : Math.Max(0, baseAge - Int(0, 30));
                        if (age >= 1)
                        {
                            Remember(prevDev, si, cve.Id, cve.Sev);
                        }

                        rows.Add(new JsonObject
                        {
                            ["deviceId"] = id,
                            ["deviceName"] = name,
                            ["osPlatform"] = os,
                            ["rbacGroupName"] = group.Group,
                            ["softwareVendor"] = sw.Vendor,
                            ["softwareName"] = sw.Name,
                            ["softwareVersion"] = sw.Versions[vi],
                            ["cveId"] = cve.Id,
                            ["vulnerabilitySeverityLevel"] = cve.Sev,
                            ["cvssScore"] = cve.Cvss,
                            ["exploitabilityLevel"] = cve.Exp,
                            ["securityUpdateAvailable"] = !sw.EndOfSupport,
                            ["recommendationReference"] = $"va-_-{sw.Vendor}-_-{sw.Name}",
                            ["firstSeenTimestamp"] = now.AddDays(-age).AddHours(-Int(0, 24)).ToString("o", CultureInfo.InvariantCulture),
                            ["diskPaths"] = new JsonArray([.. disk.Select(x => (JsonNode)x)]),
                            ["registryPaths"] = new JsonArray([.. reg.Select(x => (JsonNode)x)]),
                            ["recommendedSecurityUpdateId"] = kb?.ToString(CultureInfo.InvariantCulture),
                            ["recommendedSecurityUpdate"] = kb is null ? string.Empty : "Oktober 2026 Sicherheitsupdates",
                            ["recommendedSecurityUpdateUrl"] = kb is null ? string.Empty : $"https://catalog.update.microsoft.com/Search.aspx?q=KB{kb}",
                        });
                    }
                }
            }
        }

        var recommendations = Catalog.Select(sw => (JsonNode)new JsonObject
        {
            ["id"] = $"va-_-{sw.Vendor}-_-{sw.Name}",
            ["recommendationName"] = sw.Recommendation,
            ["recommendedVersion"] = sw.RecommendedVersion ?? string.Empty,
        }).ToList();

        var changes = new List<JsonNode>();
        for (var back = 13; back >= 0; back--)
        {
            var date = DateTime.Today.AddDays(-back);
            var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var patchDay = date.DayOfWeek == DayOfWeek.Wednesday;
            foreach (var (status, n) in new[] { ("New", weekend ? Int(5, 40) : Int(80, 320)), ("Fixed", weekend ? Int(0, 20) : patchDay ? Int(400, 900) : Int(60, 260)) })
            {
                for (var i = 0; i < n; i++)
                {
                    var p = rnd();
                    changes.Add(new JsonObject
                    {
                        ["status"] = status,
                        ["eventTimestamp"] = date.AddMinutes(Int(0, 1440)).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                        ["vulnerabilitySeverityLevel"] = Sevs[p < 0.1 ? 3 : p < 0.5 ? 2 : p < 0.9 ? 1 : 0],
                    });
                }
            }
        }

        return (rows, machines, recommendations, changes, previous);
    }

    /// <summary>History simulated backwards from the current state (demo only).</summary>
    public static List<Snapshot> History(Snapshot snapshot)
    {
        var rnd = Rng(99);
        var current = snapshot.S.ToArray();
        var result = new List<Snapshot>();
        for (var back = 0; back < 60; back++)
        {
            var date = DateTime.Today.AddDays(-back);
            var total = current.Sum();
            result.Insert(0, new Snapshot { D = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), T = total, S = [.. current], E = (int)Math.Round(total * 0.18), Dv = snapshot.Dv, C = snapshot.C });
            var factor = date.DayOfWeek == DayOfWeek.Wednesday ? 0.88 : 1.01 + (rnd() * 0.015);
            current = [.. current.Select(x => (int)Math.Round(x * factor / 1.012))];
        }

        return result;
    }

    /// <summary>mulberry32, so the demo looks the same every time.</summary>
    private static Func<double> Rng(uint seed) => () =>
    {
        unchecked
        {
            seed += 0x6D2B79F5;
            var t = (seed ^ (seed >> 15)) * (1 | seed);
            t = (t + ((t ^ (t >> 7)) * (61 | t))) ^ t;
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    };

    private static string TitleCase(string s) =>
        string.Join(' ', s.Replace('_', ' ').Replace('-', ' ').Split(' ').Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w));

    private sealed record Product(string Vendor, string Name, string Recommendation, string[] Versions, int Cves, string? RecommendedVersion = null, string? Os = null, double Pc = 0, double Ps = 0, string? Path = null, string? UserPath = null, bool EndOfSupport = false);
}
