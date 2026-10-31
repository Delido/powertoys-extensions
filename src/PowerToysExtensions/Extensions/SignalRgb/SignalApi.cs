using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.Win32;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.SignalRgb;

public sealed class SignalSettings : SettingsBase
{
    private readonly TextSetting _api = new("apiUrl", "SignalRGB API URL", "Local REST API of SignalRGB (lighting, layouts)", "http://127.0.0.1:16038");
    private readonly TextSetting _webPipe = new("webPipeUrl", "SignalRGB WebPipe URL", "Local WebSocket of SignalRGB that provides the sensor data", "ws://127.0.0.1:16036");
    private readonly ToggleSetting _service = new("companionService", "Run the companion service", "Keeps the companion helper running (checked every minute): mic mute mirror, battery alert, keyboard game mode, CS2 integration, TeamSpeak mute, audio switching", true);
    private readonly ToggleSetting _micHw = new("micHardwareToWindows", "Headset mute button mutes the Windows microphone", "Discord, Teams and other apps then get no microphone signal", true);
    private readonly ToggleSetting _micWin = new("micWindowsToHardware", "Windows mute also mutes the headset", "Mutes at device level, which apps with raw capture can't bypass", true);
    private readonly ToggleSetting _ts = new("teamspeak", "Headset mute also mutes TeamSpeak", "Needs TeamSpeak 5/6 with Remote Apps; set it up once in “Headset & Companion”", true);
    private readonly ToggleSetting _tsFollow = new("teamspeakFollow", "TeamSpeak mute also mutes headset and Windows", "Muting in TeamSpeak itself mutes the Windows microphone and the headset too", true);
    private readonly ToggleSetting _battery = new("batteryAlert", "Play a sound when the headset battery is low", "Checked every minute, confirmed by re-reads before alerting", true);
    private readonly ToggleSetting _batteryNotify = new("batteryNotify", "Also show a Windows notification for low battery", string.Empty, true);
    private readonly TextSetting _threshold = new("batteryThreshold", "Battery alert below (%)", "Alert when the battery is at or below this level", "20");
    private readonly TextSetting _reAlert = new("batteryReAlert", "Repeat alert every (min)", "0 = only once until the headset is charged", "30");
    private readonly ToggleSetting _gameMode = new("gameMode", "Keyboard hardware game mode while a game runs", "Games are detected by the Power Plan extension", true);
    private readonly ToggleSetting _cs2 = new("cs2", "Counter-Strike 2 Game State Integration", "Receiver on port 3000 and HTTPS bridge on 3443 for the CS2 Reactive effect, only while cs2.exe runs", true);

    public SignalSettings()
        : base("signalrgb")
    {
        Settings.Add(_api);
        Settings.Add(_webPipe);
        Settings.Add(_service);
        Settings.Add(_micHw);
        Settings.Add(_micWin);
        Settings.Add(_ts);
        Settings.Add(_tsFollow);
        Settings.Add(_battery);
        Settings.Add(_batteryNotify);
        Settings.Add(_threshold);
        Settings.Add(_reAlert);
        Settings.Add(_gameMode);
        Settings.Add(_cs2);
        Load();
    }

    public string ApiUrl => Http.TrimUrl(_api.Value is { Length: > 0 } v ? v : "http://127.0.0.1:16038");

    public string WebPipeUrl => Http.TrimUrl(_webPipe.Value is { Length: > 0 } v ? v : "ws://127.0.0.1:16036") + "/";

    public bool ServiceEnabled => _service.Value;

    public bool BatteryNotify => _batteryNotify.Value;

    /// <summary>The helper config (same format as written by the Raycast extension).</summary>
    public string HelperConfig() => JsonSerializer.Serialize(
        new
        {
            mic = new { enabled = true, hardwareToWindows = _micHw.Value, windowsToHardware = _micWin.Value },
            battery = new
            {
                enabled = _battery.Value,
                threshold = int.TryParse(_threshold.Value, out var t) ? t : 20,
                reAlertMinutes = double.TryParse(_reAlert.Value, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 30,
                confirmSamples = 2,
                pollSeconds = 60,
                soundPath = Paths.Asset(Path.Combine("signalrgb", "battery_low.wav")),
            },
            gameMode = new { enabled = _gameMode.Value },
            cs2 = new { enabled = _cs2.Value, port = 3000, httpsPort = 3443, token = "signalcompanion" },
            audio = new { rotation = Array.Empty<string>() },
            teamspeak = new { enabled = _ts.Value, followTeamSpeak = _tsFollow.Value },
        },
        new JsonSerializerOptions { WriteIndented = true });
}

public sealed record Effect(string Id, string Name, string ApplyPath);

public sealed record Lighting(bool Enabled, int GlobalBrightness, string Name);

/// <summary>Official SignalRGB local REST API (https://docs.signalrgb.com/developer/signalrgb-api/introduction/).</summary>
public sealed class SignalApi(SignalSettings settings)
{
    public SignalSettings Settings => settings;

    public async Task<Lighting> GetLighting(CancellationToken token = default)
    {
        var a = (await Request(HttpMethod.Get, "lighting", null, token))?["attributes"];
        return new Lighting(a?["enabled"]?.GetValue<bool>() ?? false, a?["global_brightness"]?.GetValue<int>() ?? 0, a?["name"]?.GetValue<string>() ?? string.Empty);
    }

    public async Task<Effect[]> Effects(CancellationToken token = default) =>
        [.. ((await Request(HttpMethod.Get, "lighting/effects", null, token))?["items"] as JsonArray ?? [])
            .Select(i => new Effect(i!["id"]!.GetValue<string>(), i["attributes"]?["name"]?.GetValue<string>() ?? string.Empty, i["links"]?["apply"]?.GetValue<string>() ?? string.Empty))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];

    public async Task<JsonNode?> EffectInfo(string id, CancellationToken token = default) =>
        (await Request(HttpMethod.Get, $"lighting/effects/{Uri.EscapeDataString(id)}", null, token))?["attributes"];

    public Task Apply(Effect effect) => Request(HttpMethod.Post, effect.ApplyPath);

    public async Task<string> Step(string direction) =>
        (await Request(HttpMethod.Post, $"lighting/{direction}"))?["attributes"]?["name"]?.GetValue<string>() ?? string.Empty;

    public Task SetEnabled(bool enabled) => Request(HttpMethod.Patch, "lighting/enabled", new JsonObject { ["enabled"] = enabled });

    public Task SetBrightness(int value) => Request(HttpMethod.Patch, "lighting/global_brightness", new JsonObject { ["global_brightness"] = value });

    public async Task<string[]> Presets(string effectId, CancellationToken token = default) =>
        [.. ((await Request(HttpMethod.Get, $"lighting/effects/{effectId}/presets", null, token))?["items"] as JsonArray ?? []).Select(i => i!["id"]!.GetValue<string>())];

    public Task ApplyPreset(string effectId, string preset) => Request(HttpMethod.Patch, $"lighting/effects/{effectId}/presets", new JsonObject { ["preset"] = preset });

    public async Task<(string[] Layouts, string Current)> Layouts(CancellationToken token = default)
    {
        var all = Request(HttpMethod.Get, "scenes/layouts", null, token);
        var current = Request(HttpMethod.Get, "scenes/current_layout", null, token);
        return ([.. ((await all)?["items"] as JsonArray ?? []).Select(i => i!["id"]!.GetValue<string>())], (await current)?["current_layout"]?["id"]?.GetValue<string>() ?? string.Empty);
    }

    public Task SetLayout(string layout) => Request(HttpMethod.Patch, "scenes/current_layout", new JsonObject { ["layout"] = layout });

    private async Task<JsonNode?> Request(HttpMethod method, string path, JsonObject? body = null, CancellationToken token = default)
    {
        var request = new HttpRequestMessage(method, $"{settings.ApiUrl}{(path.StartsWith("/api/", StringComparison.Ordinal) ? path : $"/api/v1/{path}")}");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await Http.Client().SendAsync(request, token);
        }
        catch (HttpRequestException)
        {
            throw new ApiException("SignalRGB is not running (API not reachable)");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(token);
            var json = text.Length > 0 ? JsonNode.Parse(text) : null;
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 403)
                {
                    throw new ApiException("This needs SignalRGB Pro and a signed-in account");
                }

                var error = json?["errors"]?[0];
                throw new ApiException(error?["detail"]?.GetValue<string>() ?? error?["title"]?.GetValue<string>() ?? $"{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            return json?["data"];
        }
    }
}

/// <summary>
/// The companion helper (Helpers\signal-helper.exe, source in helpers/signal-helper) on port 3010:
/// mic mute mirror, battery alert, keyboard game mode, CS2 integration, TeamSpeak and the audio router.
/// The same helper serves the Raycast extension; whichever launcher finds it missing starts it.
/// </summary>
public sealed class Companion(SignalSettings settings)
{
    public const string Url = "http://127.0.0.1:3010";
    private const string CfgName = "gamestate_integration_signalcompanion.cfg";

    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // Reuse the Raycast extension's data folder when it exists, so TeamSpeak approval, certificates and log stay
    private static readonly string DataDir = Directory.Exists(Path.Combine(LocalAppData, "Raycast", "extensions", "signalrgb", "helper"))
        ? Path.Combine(LocalAppData, "Raycast", "extensions", "signalrgb", "helper")
        : Path.Combine(LocalAppData, "SignalRGBHelper");

    private readonly string _configPath = Path.Combine(Paths.For("signalrgb"), "helper-config.json");

    public SignalSettings Settings => settings;

    public async Task<JsonNode?> Status()
    {
        try
        {
            return await Call("/api/status", HttpMethod.Get, TimeSpan.FromSeconds(1.5));
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Writes the helper config; a running helper picks up changes within 3 seconds.</summary>
    public bool WriteConfig()
    {
        var text = settings.HelperConfig();
        var current = File.Exists(_configPath) ? File.ReadAllText(_configPath) : string.Empty;
        if (current == text)
        {
            return false;
        }

        File.WriteAllText(_configPath, text);
        return true;
    }

    public async Task Start()
    {
        WriteConfig();
        Directory.CreateDirectory(DataDir);
        var exe = Path.Combine(Paths.App, "Helpers", "signal-helper.exe");
        if (!File.Exists(exe))
        {
            throw new InvalidOperationException("signal-helper.exe is missing in this build");
        }

        var result = await Proc.Run(exe, ["launch", "-config", _configPath, "-data", DataDir], TimeSpan.FromSeconds(10));
        if (result.Output.Contains("\"ok\":false", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(JsonNode.Parse(result.Output)?["error"]?.GetValue<string>() ?? "launch failed");
        }

        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250);
            if (await Status() is not null)
            {
                return;
            }
        }

        throw new InvalidOperationException("The helper did not start. Is SignalCompanion still running (port 3010)?");
    }

    public Task Stop() => Call("/api/quit", HttpMethod.Post);

    public async Task Restart()
    {
        try
        {
            await Stop();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            // not running
        }

        await Task.Delay(800);
        await Start();
    }

    public async Task<bool> SetMic(string muted) => (await Call($"/api/mic?muted={muted}", HttpMethod.Post))?["mic"]?["muted"]?.GetValue<bool>() ?? false;

    public async Task<int> ReadBattery() => (await Call("/api/battery", HttpMethod.Post, TimeSpan.FromSeconds(5)))?["battery"]?.GetValue<int>() ?? -1;

    public Task PressTeamSpeakKey(int delay) => Call($"/api/teamspeak/press?delay={delay}", HttpMethod.Post);

    public Task TestTeamSpeakKey() => Call("/api/teamspeak/test", HttpMethod.Post);

    public Task TestSound() => Call("/api/test-sound", HttpMethod.Post);

    public async Task<string[]> Log(int lines = 300) =>
        [.. ((await Call($"/api/log?lines={lines}", HttpMethod.Get))?["lines"] as JsonArray ?? []).Select(l => l?.GetValue<string>() ?? string.Empty)];

    public async Task<(string Id, string Name, bool Default)[]> AudioDevices() =>
        [.. ((await Call("/devices", HttpMethod.Get))?["devices"] as JsonArray ?? []).Select(d => (d!["id"]!.GetValue<string>(), d["name"]?.GetValue<string>() ?? string.Empty, d["default"]?.GetValue<bool>() ?? false))];

    public async Task<string?> CurrentAudioDevice() => (await Call("/current", HttpMethod.Get))?["current"]?["name"]?.GetValue<string>();

    public Task SetAudioDevice(string id) => Call($"/set?id={Uri.EscapeDataString(id)}", HttpMethod.Get);

    public Task NextAudioDevice() => Call("/next", HttpMethod.Get);

    /// <summary>"Kopfhörer (CORSAIR VIRTUOSO …)" -> name and hardware part.</summary>
    public static (string Title, string Subtitle) SplitDeviceName(string name)
    {
        var m = Regex.Match(name, @"^(.*?)\s*\((.*)\)$");
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (name, string.Empty);
    }

    // ---------- CS2 setup (same files as SignalCompanion, so existing installs keep working) ----------
    public static string? Cs2CfgDir()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
            ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (steam is null)
        {
            return null;
        }

        var roots = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            roots.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal)));
        }

        return roots.Select(r => Path.Combine(r, "steamapps", "common", "Counter-Strike Global Offensive", "game", "csgo", "cfg")).FirstOrDefault(Directory.Exists);
    }

    public static string? Cs2CfgInstalled() => Cs2CfgDir() is { } dir && File.Exists(Path.Combine(dir, CfgName)) ? Path.Combine(dir, CfgName) : null;

    public static string InstallCs2Cfg(int port, string token)
    {
        var dir = Cs2CfgDir() ?? throw new InvalidOperationException("CS2 cfg folder not found (is CS2 installed via Steam?)");
        var content = $$"""
"SignalCompanion CS2 Integration v1.2"
{
    "uri"       "http://127.0.0.1:{{port}}"
    "timeout"   "5.0"
    "buffer"    "0.0"
    "throttle"  "0.1"
    "heartbeat" "10.0"
    "auth"
    {
        "token" "{{token}}"
    }
    "data"
    {
        "provider"            "1"
        "map"                 "1"
        "round"               "1"
        "player_id"           "1"
        "player_state"        "1"
        "player_weapons"      "1"
        "player_match_stats"  "1"
        "bomb"                "1"
        "phase_countdowns"    "1"
    }
}

""";
        var path = Path.Combine(dir, CfgName);
        File.WriteAllText(path, content.Replace("\r\n", "\n", StringComparison.Ordinal));
        return path;
    }

    public static void UninstallCs2Cfg()
    {
        if (Cs2CfgInstalled() is { } path)
        {
            File.Delete(path);
        }
    }

    private static string EffectsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WhirlwindFX", "Effects");

    public static bool EffectInstalled() => File.Exists(Path.Combine(EffectsDir, "cs2_reactive.html"));

    public static string InstallEffect()
    {
        Directory.CreateDirectory(EffectsDir);
        var source = Paths.Asset(Path.Combine("signalrgb", "cs2-effect"));

        // SignalRGB only activates effects with CRLF line endings
        var html = Regex.Replace(File.ReadAllText(Path.Combine(source, "cs2_reactive.html")), "\r?\n", "\r\n");
        File.WriteAllText(Path.Combine(EffectsDir, "cs2_reactive.html"), html, new UTF8Encoding(false));
        File.Copy(Path.Combine(source, "cs2_reactive.png"), Path.Combine(EffectsDir, "cs2_reactive.png"), overwrite: true);
        return Path.Combine(EffectsDir, "cs2_reactive.html");
    }

    private static async Task<JsonNode?> Call(string path, HttpMethod method, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2.5));
        HttpResponseMessage response;
        try
        {
            response = await Http.Client().SendAsync(new HttpRequestMessage(method, $"{Url}{path}"), cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ApiException("Companion service is not running");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cts.Token);
            JsonNode? json = null;
            try
            {
                json = text.Length > 0 ? JsonNode.Parse(text) : null;
            }
            catch (JsonException)
            {
                // not JSON
            }

            if (!response.IsSuccessStatusCode || json?["ok"]?.GetValue<bool>() == false)
            {
                throw new ApiException(json?["error"]?.GetValue<string>() ?? $"{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            return json;
        }
    }
}
