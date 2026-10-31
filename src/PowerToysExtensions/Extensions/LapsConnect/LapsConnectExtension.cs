using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.LapsConnect;

[Guid("4535C230-FEAA-44FF-9A8C-01020DAAC142")]
public sealed partial class LapsConnectExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new LapsProvider();
}

public sealed class LapsSettings : SettingsBase
{
    private readonly TextSetting _tenant = new("tenantId", "Tenant ID", "Directory (tenant) ID of the LAPS app registration", string.Empty);
    private readonly TextSetting _client = new("clientId", "Client ID", "Application (client) ID of the LAPS app registration (redirect URI ms-appx-web://microsoft.aad.brokerplugin/<client id>)", string.Empty);
    private readonly TextSetting _unifi = new("unifiUrl", "UniFi URL", "Address of the UniFi gateway running the WireGuard server", string.Empty) { Placeholder = "https://192.168.1.1" };
    private readonly ChoiceSetSetting _autoLock = new("autoLock", "Auto-lock after", "LAPS Connect locks again after this time without use. The UniFi API key is stored encrypted with your LAPS Connect password.", Choices(("5 minutes", "5"), ("1 minute", "1"), ("15 minutes", "15"), ("30 minutes", "30"), ("1 hour", "60")));
    private readonly ToggleSetting _signOut = new("signOutOnLock", "Sign out of Microsoft when locking", "Every unlock then needs a fresh Microsoft sign-in (incl. MFA) before a LAPS password can be read", false);
    private readonly ToggleSetting _insecure = new("unifiInsecure", "Accept the UniFi gateway's self-signed certificate", "UniFi gateways use a self-signed certificate by default", true);
    private readonly TextSetting _jump = new("jumpHost", "Jump host", "Optional SSH jump host for SSH and RDP: an alias from ~/.ssh/config or user@host:port", string.Empty);
    private readonly TextSetting _filter = new("nameFilter", "WireGuard name filter", "Regular expression for WireGuard clients to show (default: X-NAME without suffix)", "^.-[^-]+$");
    private readonly TextSetting _suffix = new("deviceSuffix", "Windows device suffix", "Computer name = WireGuard name + this suffix", "-LAP");
    private readonly ChoiceSetSetting _clipboard = new("clipboardSeconds", "Clear clipboard after", "The LAPS password is removed from the clipboard after this time", Choices(("30 seconds", "30"), ("15 seconds", "15"), ("60 seconds", "60"), ("2 minutes", "120")));
    private readonly ChoiceSetSetting _terminal = new("terminal", "Terminal", "Terminal for SSH sessions", Terminal.Choices);

    public LapsSettings()
        : base("laps-connect")
    {
        Settings.Add(_tenant);
        Settings.Add(_client);
        Settings.Add(_unifi);
        Settings.Add(_autoLock);
        Settings.Add(_signOut);
        Settings.Add(_insecure);
        Settings.Add(_jump);
        Settings.Add(_filter);
        Settings.Add(_suffix);
        Settings.Add(_clipboard);
        Settings.Add(_terminal);
        Load();
    }

    public string TenantId => _tenant.Value ?? string.Empty;

    public string ClientId => _client.Value ?? string.Empty;

    public string UnifiUrl => Http.TrimUrl(_unifi.Value is { Length: > 0 } u && !u.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? $"https://{u}" : _unifi.Value ?? string.Empty);

    public TimeSpan AutoLock => TimeSpan.FromMinutes(int.TryParse(_autoLock.Value, out var m) ? m : 5);

    public bool SignOutOnLock => _signOut.Value;

    public bool UnifiInsecure => _insecure.Value;

    public string? JumpHost => string.IsNullOrWhiteSpace(_jump.Value) ? null : _jump.Value.Trim();

    public Regex NameFilter
    {
        get
        {
            try
            {
                return new Regex(string.IsNullOrWhiteSpace(_filter.Value) ? "^.-[^-]+$" : _filter.Value.Trim(), RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException($"Invalid WireGuard name filter: {_filter.Value}");
            }
        }
    }

    public string Suffix => _suffix.Value?.Trim() ?? "-LAP";

    public TimeSpan ClipboardTime => TimeSpan.FromSeconds(int.TryParse(_clipboard.Value, out var s) ? s : 30);

    public string TerminalChoice => _terminal.Value ?? "auto";
}

public sealed record WireGuardUser(string Id, string Name, string Ip, string Network, bool? Online);

public sealed record LapsCredential(string DeviceName, string AccountName, string Password, DateTimeOffset? BackupDateTime);

public sealed class LapsApi
{
    public LapsApi(LapsSettings settings)
    {
        Settings = settings;
        Vault = new Vault("laps-connect", () => settings.AutoLock);

        // Delegated only. Reading a password additionally requires an Entra role with
        // microsoft.directory/deviceLocalCredentials/password/read for the signed-in admin.
        Auth = new MicrosoftAuth("laps", () => settings.TenantId, () => settings.ClientId, ["https://graph.microsoft.com/User.Read", "https://graph.microsoft.com/DeviceLocalCredential.Read.All"]);
        Vault.Locked += () =>
        {
            if (settings.SignOutOnLock)
            {
                _ = Auth.SignOut();
            }
        };
    }

    public LapsSettings Settings { get; }

    public Vault Vault { get; }

    public MicrosoftAuth Auth { get; }

    public string DeviceName(WireGuardUser user) => $"{user.Name}{Settings.Suffix}".ToUpperInvariant();

    /// <summary>Checks an API key before it is stored (setup).</summary>
    public async Task VerifyUnifiKey(string key) => await Unifi("/proxy/network/api/s/default/rest/networkconf", key.Trim());

    /// <summary>WireGuard users from the (undocumented) v2 API of UniFi Network, with their fixed tunnel IP.</summary>
    public async Task<List<WireGuardUser>> WireGuardUsers(CancellationToken token)
    {
        var key = Vault.Secret;
        var networks = Rows(await Unifi("/proxy/network/api/s/default/rest/networkconf", key, token))
            .Where(n => Regex.IsMatch($"{n["vpn_type"]} {n["purpose"]}", "wireguard", RegexOptions.IgnoreCase) && !Regex.IsMatch(n["vpn_type"]?.ToString() ?? string.Empty, "client", RegexOptions.IgnoreCase))
            .ToList();
        if (networks.Count == 0)
        {
            throw new InvalidOperationException("UniFi: no WireGuard server network found");
        }

        var users = new List<WireGuardUser>();
        foreach (var net in networks)
        {
            var id = net["_id"]?.ToString();
            var list = Rows(await Unifi($"/proxy/network/v2/api/site/default/wireguard/{id}/users?networkId={id}", key, token));
            if (list.Count == 0 && users.Count == 0)
            {
                throw new InvalidOperationException("UniFi: WireGuard user list is empty or the API changed (v2 wireguard/users)");
            }

            users.AddRange(list.Where(u => u["name"] is not null && u["interface_ip"] is not null)
                .Select(u => new WireGuardUser(u["_id"]!.ToString(), u["name"]!.ToString(), u["interface_ip"]!.ToString(), net["name"]?.ToString() ?? string.Empty, null)));
        }

        // active VPN peers (legacy stat API, one row per connected peer); if it fails, the status stays unknown
        try
        {
            var peers = Rows(await Unifi("/proxy/network/api/s/default/stat/remoteuservpn", key, token))
                .Where(p => p["up"]?.GetValue<bool>() != false && Regex.IsMatch(p["vpn_type"]?.ToString() ?? "wireguard", "wireguard", RegexOptions.IgnoreCase))
                .ToList();
            var names = peers.Select(p => (p["user"]?.ToString() ?? string.Empty).ToLowerInvariant()).ToHashSet();
            var ips = peers.Select(p => p["tunnel_ip"]?.ToString()).ToHashSet();
            users = [.. users.Select(u => u with { Online = names.Contains(u.Name.ToLowerInvariant()) || ips.Contains(u.Ip) })];
        }
        catch (Exception ex) when (ex is ApiException or InvalidOperationException)
        {
            // leave unknown
        }

        var filter = Settings.NameFilter;
        return [.. users.Where(u => filter.IsMatch(u.Name)).OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The current LAPS password of a device. Every call is recorded in the Entra audit log.</summary>
    public async Task<LapsCredential> Password(string deviceName)
    {
        _ = Vault.Secret; // throws while locked
        var name = deviceName.Replace("'", "''", StringComparison.Ordinal);
        var list = await Graph($"directory/deviceLocalCredentials?$filter={Uri.EscapeDataString($"deviceName eq '{name}'")}");
        var entries = list["value"] as JsonArray ?? [];
        var entry = entries.FirstOrDefault(d => string.Equals(d?["deviceName"]?.ToString(), deviceName, StringComparison.OrdinalIgnoreCase)) ?? entries.FirstOrDefault()
            ?? throw new InvalidOperationException($"No LAPS password stored for {deviceName}");
        var detail = await Graph($"directory/deviceLocalCredentials/{entry["id"]}?$select=credentials,deviceName");
        var latest = (detail["credentials"] as JsonArray ?? []).Where(c => c is not null).OrderByDescending(c => c!["backupDateTime"]?.ToString()).FirstOrDefault()
            ?? throw new InvalidOperationException($"No LAPS password stored for {deviceName}");
        return new LapsCredential(
            detail["deviceName"]?.ToString() ?? deviceName,
            latest["accountName"]!.ToString(),
            Encoding.UTF8.GetString(Convert.FromBase64String(latest["passwordBase64"]!.ToString())),
            DateTimeOffset.TryParse(latest["backupDateTime"]?.ToString(), out var d) ? d : null);
    }

    private static List<JsonNode> Rows(JsonNode? json) => [.. (json as JsonArray ?? json?["data"] as JsonArray ?? []).Where(n => n is not null).Select(n => n!)];

    private async Task<JsonNode?> Unifi(string path, string key, CancellationToken token = default)
    {
        if (Settings.UnifiUrl.Length == 0)
        {
            throw new InvalidOperationException("Enter the UniFi URL in the settings of this extension");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, $"{Settings.UnifiUrl}{path}");
        request.Headers.Add("X-API-KEY", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await Http.Send(Http.Client(Settings.UnifiInsecure), request, token);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
        }
        catch (ApiException ex) when (ex.Status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("UniFi: API key rejected");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new InvalidOperationException("UniFi: unexpected response (is the URL correct?)");
        }
    }

    private async Task<JsonNode> Graph(string path)
    {
        var request = Http.Request(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/{path}", new AuthenticationHeaderValue("Bearer", await Auth.Token()));

        // required by the deviceLocalCredentials API
        request.Headers.Add("ocp-client-name", "PowerToys LAPS Connect");
        request.Headers.Add("ocp-client-version", "1.0");
        try
        {
            using var response = await Http.Send(Http.Client(), request);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException($"No permission to read LAPS passwords ({ex.Message}). Check your Entra role.");
        }
    }
}

public sealed partial class LapsProvider : CommandProvider
{
    private readonly ICommandItem[] _commands;

    public LapsProvider()
    {
        var api = new LapsApi(new LapsSettings());
        var gate = new VaultGate(api.Vault, "LAPS Connect", "UniFi API Key", "UniFi Network: Settings → Control Plane → Integrations", api.VerifyUnifiKey);
        Id = "delido.laps-connect";
        DisplayName = "LAPS Connect";
        Icon = Ui.Glyph("");
        Settings = api.Settings.Settings;
        _commands =
        [
            new CommandItem(new ConnectPage(api, gate)) { Title = "LAPS Connect", Subtitle = "WireGuard device → LAPS password → SSH or RDP" },
            new CommandItem(new AnonymousCommand(() =>
            {
                api.Vault.LockNow();
                Status.Info("🔒 LAPS Connect locked");
            })
            { Id = "lock", Name = "Lock", Result = CommandResult.Dismiss() })
            {
                Title = "Lock LAPS Connect",
                Icon = Ui.Glyph(""),
            },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem? GetCommandItem(string id) => _commands.FirstOrDefault(c => c.Command?.Id == id);
}

internal sealed partial class ConnectPage : AsyncListPage
{
    private readonly LapsApi _api;
    private readonly VaultGate _gate;

    public ConnectPage(LapsApi api, VaultGate gate)
    {
        _api = api;
        _gate = gate;
        Id = "connect";
        Name = "Open";
        Title = "LAPS Connect";
        Icon = Ui.Glyph("");
        PlaceholderText = "Search user (WireGuard name) …";
        MaxAge = TimeSpan.FromMinutes(1);
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        if (_gate.Items(Reload) is { } gate)
        {
            return new(gate);
        }

        return new([.. (await _api.WireGuardUsers(token)).Select(Item)]);
    }

    protected override ICommandItem? Empty() =>
        !_api.Auth.Configured || _api.Settings.UnifiUrl.Length == 0 ? Ui.SetupItem("delido.laps-connect", "LAPS Connect (Tenant, Client ID, UniFi URL)") : Ui.EmptyItem("No WireGuard users found", string.Empty, "");

    private string[] JumpArgs() => _api.Settings.JumpHost is { } jump ? ["-J", Terminal.SafeArg(jump)] : [];

    /// <summary>Fetches the LAPS password (status shown while Microsoft sign-in may ask), then uses it.</summary>
    private void WithLaps(WireGuardUser user, Func<LapsCredential, Task> use) =>
        Status.Start($"Fetching LAPS password for {_api.DeviceName(user)} …", "Done", async () => await use(await _api.Password(_api.DeviceName(user))));

    private void SshWithLaps(WireGuardUser user) => WithLaps(user, cred =>
    {
        Secrets.CopyToClipboard(cred.Password, _api.Settings.ClipboardTime);
        Terminal.Open(cred.DeviceName, [
            "ssh", .. JumpArgs(),

            // known_hosts entry follows the device, not the (reassignable) WireGuard IP
            "-o", $"HostKeyAlias={cred.DeviceName}",
            "-o", "PreferredAuthentications=keyboard-interactive,password",
            "-l", Terminal.SafeArg(cred.AccountName), user.Ip], _api.Settings.TerminalChoice);
        Status.Info($"LAPS password on the clipboard for {_api.Settings.ClipboardTime.TotalSeconds:0}s – right-click to paste");
        return Task.CompletedTask;
    });

    private void SshAs(WireGuardUser user, string? login)
    {
        try
        {
            string[] args = login is null ? ["ssh", .. JumpArgs(), "-o", $"HostKeyAlias={_api.DeviceName(user)}", user.Ip] : ["ssh", .. JumpArgs(), "-o", $"HostKeyAlias={_api.DeviceName(user)}", "-l", Terminal.SafeArg(login), user.Ip];
            Terminal.Open(user.Name, args, _api.Settings.TerminalChoice);
        }
        catch (InvalidOperationException ex)
        {
            Status.Error(ex.Message);
        }
    }

    /// <summary>RDP with the LAPS account; via the jump host a local SSH tunnel is opened first.</summary>
    private void RdpWithLaps(WireGuardUser user) => WithLaps(user, async cred =>
    {
        var address = user.Ip;
        if (_api.Settings.JumpHost is { } jump)
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            Terminal.Open($"RDP tunnel {cred.DeviceName}", ["ssh", "-N", "-o", "ExitOnForwardFailure=yes", "-L", $"127.0.0.1:{port}:{user.Ip}:3389", Terminal.SafeArg(jump)], _api.Settings.TerminalChoice);
            Status.Info("Waiting for the RDP tunnel … log in to the jump host in the terminal tab if asked");
            var until = DateTime.UtcNow.AddMinutes(2);
            while (!await PortOpen(port))
            {
                if (DateTime.UtcNow > until)
                {
                    throw new InvalidOperationException("The RDP tunnel did not come up");
                }

                await Task.Delay(1000);
            }

            address = $"127.0.0.1:{port}";
        }

        Secrets.StartRdp(address, $".\\{cred.AccountName}", cred.Password);
    });

    private static async Task<bool> PortOpen(int port)
    {
        using var client = new TcpClient();
        try
        {
            using var cts = new CancellationTokenSource(800);
            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private ListItem Item(WireGuardUser user)
    {
        var device = _api.DeviceName(user);
        var asUser = new FormBuilder()
            .Text($"{user.Ip} ({device})", subtle: true)
            .Input("login", "User Name", placeholder: @"admin, DOMAIN\user or user@domain", required: true)
            .Text("The password is asked in the terminal.", subtle: true)
            .Build("Connect");
        var tags = new List<ITag>();
        if (user.Online == true)
        {
            tags.Add(Ui.Green("online"));
        }
        else if (user.Online == false)
        {
            tags.Add(Ui.Red("offline"));
        }

        tags.Add(Ui.Gray(device));
        return new ListItem(new AnonymousCommand(() => SshWithLaps(user)) { Name = "SSH with LAPS", Result = CommandResult.Dismiss() })
        {
            Title = user.Name,
            Subtitle = user.Ip,
            Icon = Ui.Glyph(""),
            Tags = [.. tags],
            MoreCommands =
            [
                Ui.Action("RDP with LAPS", "", () => RdpWithLaps(user), Ui.Ctrl(VirtualKey.R), CommandResult.Dismiss()),
                Ui.Action("SSH with Own Login", "", () => SshAs(user, null), Ui.Ctrl(VirtualKey.O), CommandResult.Dismiss()),
                new CommandContextItem(new FormPage($"SSH to {device}", asUser, v =>
                {
                    SshAs(user, v.Str("login"));
                    return CommandResult.Dismiss();
                })) { Title = "SSH as Other User …", RequestedShortcut = Ui.CtrlShift(VirtualKey.O) },
                new CommandContextItem(new PasswordPage(_api, device)) { Title = "Show LAPS Password", RequestedShortcut = Ui.CtrlShift(VirtualKey.P) },
                Ui.Copy(user.Ip, "Copy IP", Ui.CtrlShift(VirtualKey.C)),
                Ui.Copy(device, "Copy Device Name", Ui.CtrlShift((VirtualKey)0xBE)),
                Ui.Action("Refresh", "", Reload, Ui.CtrlShift(VirtualKey.R)),
                Ui.Action("Sign out of Microsoft", "", () => Status.Start("Signing out …", "Signed out", _api.Auth.SignOut)),
                .. _gate.SecurityActions(Reload),
            ],
        };
    }

    /// <summary>Shows account and password (the retrieval is recorded in the Entra audit log).</summary>
    private sealed partial class PasswordPage(LapsApi api, string device) : AsyncMarkdownPage
    {
        protected override async Task<(string Markdown, IDetails? Details, IContextItem[] Commands)> Load(CancellationToken token)
        {
            Name = "Show LAPS Password";
            Title = $"LAPS {device}";
            var cred = await api.Password(device);
            var markdown = $"# {cred.DeviceName}\n\n| | |\n|---|---|\n| Account | `{cred.AccountName}` |\n| Password | `{cred.Password.Replace("`", "ˋ", StringComparison.Ordinal)}` |\n| Backed up | {cred.BackupDateTime?.ToLocalTime().ToString("g") ?? "-"} |\n\n_This retrieval is recorded in the Entra audit log. Close this view when done._";
            return (markdown, null, [
                Ui.Action("Copy Password (Auto-Clear)", "", () =>
                {
                    Secrets.CopyToClipboard(cred.Password, api.Settings.ClipboardTime);
                    Status.Info($"Password on the clipboard for {api.Settings.ClipboardTime.TotalSeconds:0}s");
                }, Ui.CtrlShift(VirtualKey.C)),
                Ui.Copy(cred.AccountName, "Copy Account Name")]);
        }
    }
}
