using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.Teams;

public sealed class TeamsSettings : SettingsBase
{
    private readonly TextSetting _tenant = new("tenantId", "Tenant ID", "Directory (tenant) ID of your Microsoft 365 tenant (Entra ID → Overview); empty = any work account", string.Empty);
    private readonly TextSetting _client = new("clientId", "Client ID", "Application (client) ID of the Entra ID app registration (redirect URI ms-appx-web://microsoft.aad.brokerplugin/<client id>)", string.Empty);
    private readonly ChoiceSetSetting _openIn = new("openIn", "Open links in", "Where chats, channels and meetings open", Choices(("Teams app", "app"), ("Browser", "browser")));

    public TeamsSettings()
        : base("teams")
    {
        Settings.Add(_tenant);
        Settings.Add(_client);
        Settings.Add(_openIn);
        Load();
    }

    public string TenantId => string.IsNullOrWhiteSpace(_tenant.Value) ? "organizations" : _tenant.Value.Trim();

    public string ClientId => _client.Value ?? string.Empty;

    public bool OpenInBrowser => _openIn.Value == "browser";
}

public sealed record Person(string Id, string Name, string Email, string Upn, string JobTitle, string Department, string Office, string Phone);

public sealed record Meeting(string Id, string Subject, DateTimeOffset Start, DateTimeOffset End, bool AllDay, string? JoinUrl, string Organizer, string WebLink);

public sealed record PresenceInfo(string Label, Tag Tag);

public sealed partial class TeamsApi
{
    private static readonly string[] Scopes =
    [
        "User.Read",
        "User.ReadBasic.All",
        "People.Read",
        "Presence.Read.All",
        "Presence.ReadWrite",
        "Team.ReadBasic.All",
        "Channel.ReadBasic.All",
        "Chat.Read",
        "Chat.Create",
        "ChatMessage.Send",
        "ChannelMessage.Read.All",
        "ChannelMessage.Send",
        "Calendars.Read",
    ];

    private string? _myId;

    public TeamsApi(TeamsSettings settings)
    {
        Settings = settings;
        Graph = new Graph(new MicrosoftAuth("teams", () => settings.TenantId, () => settings.ClientId, [.. Scopes.Select(s => $"https://graph.microsoft.com/{s}")]));
    }

    public TeamsSettings Settings { get; }

    public Graph Graph { get; }

    public bool Configured => Graph.Auth.Configured;

    public async Task<string> MyId(CancellationToken token = default) =>
        _myId ??= Graph.Str((await Graph.Get("me?$select=id", token))["id"]);

    /// <summary>
    /// The People API (People.Read) ranks frequent contacts first and has job title, department and phones;
    /// the directory search (User.ReadBasic.All, basic fields only) adds everyone else.
    /// </summary>
    public async Task<Person[]> SearchPeople(string text, CancellationToken token)
    {
        var term = text.Trim().Replace("\"", string.Empty, StringComparison.Ordinal);
        const string filter = "$filter=personType/subclass eq 'OrganizationUser'";
        var people = Graph.Get(term.Length > 0 ? $"me/people?$search={Uri.EscapeDataString($"\"{term}\"")}&$top=30&{filter}" : $"me/people?$top=30&{filter}", token);
        var directory = term.Length > 0
            ? Graph.Get($"users?$search={Uri.EscapeDataString($"\"displayName:{term}\" OR \"mail:{term}\"")}&$select=id,displayName,mail,userPrincipalName&$top=30", token, ("ConsistencyLevel", "eventual"))
            : Task.FromResult<JsonNode>(new JsonObject());

        var ranked = Values(await people).Select(FromPerson).ToList();
        JsonNode all;
        try
        {
            all = await directory;
        }
        catch (Exception ex) when (ex is ApiException or InvalidOperationException)
        {
            all = new JsonObject();
        }

        var seen = ranked.Select(p => p.Id).ToHashSet();
        return [.. ranked.Concat(Values(all).Select(FromUser).Where(p => seen.Add(p.Id))).Where(p => p.Upn.Length > 0)];
    }

    public async Task<Dictionary<string, JsonNode>> Presences(IEnumerable<string> ids, CancellationToken token = default)
    {
        var list = ids.Take(650).ToArray();
        if (list.Length == 0)
        {
            return [];
        }

        try
        {
            var result = await Graph.Post("communications/getPresencesByUserId", new JsonObject { ["ids"] = new JsonArray([.. list.Select(i => (JsonNode)i)]) }, token);
            return Values(result).ToDictionary(p => Graph.Str(p["id"]), p => p);
        }
        catch (Exception ex) when (ex is ApiException or InvalidOperationException)
        {
            return [];
        }
    }

    public Task<JsonNode> MyPresence(CancellationToken token) => Graph.Get("me/presence", token);

    public Task SetPresence(string availability, string activity, string? duration)
    {
        var body = new JsonObject { ["availability"] = availability, ["activity"] = activity };
        if (duration is not null)
        {
            body["expirationDuration"] = duration;
        }

        return Graph.Post("me/presence/setUserPreferredPresence", body);
    }

    public Task ResetPresence() => Graph.Post("me/presence/clearUserPreferredPresence", new JsonObject());

    public Task SetStatusMessage(string message, int hours)
    {
        var status = new JsonObject { ["message"] = new JsonObject { ["content"] = message, ["contentType"] = "text" } };
        if (hours > 0)
        {
            status["expiryDateTime"] = new JsonObject { ["dateTime"] = DateTime.UtcNow.AddHours(hours).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), ["timeZone"] = "UTC" };
        }

        return Graph.Post("me/presence/setStatusMessage", new JsonObject { ["statusMessage"] = status });
    }

    public async Task<JsonNode[]> Chats(CancellationToken token) =>
        [.. Values(await Graph.Get("me/chats?$expand=members,lastMessagePreview&$orderby=lastMessagePreview/createdDateTime desc&$top=50", token))];

    public async Task<(JsonNode Team, JsonNode[] Channels)[]> TeamsWithChannels(CancellationToken token)
    {
        var teams = Values(await Graph.Get("me/joinedTeams", token)).OrderBy(t => Graph.Str(t["displayName"]), StringComparer.CurrentCultureIgnoreCase).ToArray();
        var channels = await Task.WhenAll(teams.Select(async team =>
        {
            try
            {
                return Values(await Graph.Get($"teams/{Graph.Str(team["id"])}/channels?$select=id,displayName,description,webUrl,membershipType", token))
                    .OrderBy(c => Graph.Str(c["displayName"]) is "General" or "Allgemein" ? 0 : 1)
                    .ThenBy(c => Graph.Str(c["displayName"]), StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex) when (ex is ApiException or InvalidOperationException)
            {
                return [];
            }
        }));
        return [.. teams.Select((team, i) => (team, channels[i]))];
    }

    /// <summary>Gets (or creates) the 1:1 chat with a user; Graph returns the existing chat if there is one.</summary>
    public async Task<string> OneOnOneChat(string userId)
    {
        var members = new JsonArray([.. new[] { await MyId(), userId }.Select(id => (JsonNode)new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.aadUserConversationMember",
            ["roles"] = new JsonArray("owner"),
            ["user@odata.bind"] = $"https://graph.microsoft.com/v1.0/users('{id}')",
        })]);
        return Graph.Str((await Graph.Post("chats", new JsonObject { ["chatType"] = "oneOnOne", ["members"] = members }))["id"]);
    }

    public Task SendChatMessage(string chatId, string text) => Graph.Post($"chats/{chatId}/messages", MessageBody(text));

    public Task SendChannelMessage(string teamId, string channelId, string text) =>
        Graph.Post($"teams/{teamId}/channels/{Uri.EscapeDataString(channelId)}/messages", MessageBody(text));

    /// <summary>Upcoming events from shortly in the past up to <paramref name="days"/> ahead (not cancelled, not over).</summary>
    public async Task<Meeting[]> UpcomingMeetings(int days, CancellationToken token = default)
    {
        var start = DateTime.UtcNow.AddHours(-1).ToString("o", CultureInfo.InvariantCulture);
        var end = DateTime.UtcNow.AddDays(days).ToString("o", CultureInfo.InvariantCulture);
        const string select = "id,subject,start,end,isAllDay,isCancelled,isOnlineMeeting,onlineMeeting,location,organizer,webLink";
        var result = await Graph.Get($"me/calendarView?startDateTime={start}&endDateTime={end}&$orderby=start/dateTime&$top=100&$select={select}", token, ("Prefer", "outlook.timezone=\"UTC\""));
        return [.. Values(result)
            .Where(e => e["isCancelled"]?.GetValue<bool>() != true)
            .Select(e => new Meeting(
                Graph.Str(e["id"]),
                Graph.Str(e["subject"]) is { Length: > 0 } s ? s : "(no subject)",
                Utc(e["start"]?["dateTime"]),
                Utc(e["end"]?["dateTime"]),
                e["isAllDay"]?.GetValue<bool>() == true,
                Graph.Str(e["onlineMeeting"]?["joinUrl"]) is { Length: > 0 } join ? join : MeetupLink().Match(Graph.Str(e["location"]?["displayName"])) is { Success: true } m ? m.Value : null,
                Graph.Str(e["organizer"]?["emailAddress"]?["name"]),
                Graph.Str(e["webLink"])))
            .Where(m => m.End > DateTimeOffset.UtcNow)];
    }

    /// <summary>Opens Teams links in the desktop app (msteams:) or the browser, depending on the setting.</summary>
    public string Link(string url) => Settings.OpenInBrowser ? url : TeamsHost().Replace(url, "msteams:/");

    public string ChatWithUserLink(string upn) => Link($"https://teams.microsoft.com/l/chat/0/0?{Http.Query(("users", upn))}");

    public string CallLink(string upn, bool video) => Link($"https://teams.microsoft.com/l/call/0/0?{Http.Query(("users", upn), ("withVideo", video ? "true" : null))}");

    public static PresenceInfo? Presence(JsonNode? presence)
    {
        if (presence is null)
        {
            return null;
        }

        var availability = Graph.Str(presence["availability"]);
        var activity = Graph.Str(presence["activity"]);
        var label = activity switch
        {
            "InAMeeting" => "In a meeting",
            "InACall" => "In a call",
            _ => availability switch
            {
                "Available" => "Available",
                "AvailableIdle" => "Available (idle)",
                "Busy" => "Busy",
                "BusyIdle" => "Busy (idle)",
                "DoNotDisturb" => "Do not disturb",
                "BeRightBack" => "Be right back",
                "Away" => "Away",
                "Offline" => "Offline",
                _ => "Unknown",
            },
        };
        var tag = availability switch
        {
            "Available" or "AvailableIdle" => Ui.Green(label),
            "Busy" or "BusyIdle" or "DoNotDisturb" => Ui.Red(label),
            "BeRightBack" or "Away" => Ui.Yellow(label),
            _ => Ui.Gray(label),
        };
        return new PresenceInfo(label, tag);
    }

    private static IEnumerable<JsonNode> Values(JsonNode node) => (node["value"] as JsonArray ?? []).OfType<JsonNode>();

    private static DateTimeOffset Utc(JsonNode? value) =>
        DateTimeOffset.TryParse(Graph.Str(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : DateTimeOffset.MinValue;

    private static JsonObject MessageBody(string text) => new()
    {
        ["body"] = new JsonObject
        {
            ["contentType"] = "html",
            ["content"] = System.Net.WebUtility.HtmlEncode(text).Replace("\n", "<br>", StringComparison.Ordinal),
        },
    };

    private static Person FromPerson(JsonNode p)
    {
        var phones = (p["phones"] as JsonArray ?? []).OfType<JsonNode>().ToArray();
        var email = Graph.Str(p["mail"]) is { Length: > 0 } mail ? mail : Graph.Str(p["scoredEmailAddresses"]?[0]?["address"]);
        var upn = Graph.Str(p["userPrincipalName"]) is { Length: > 0 } u ? u : email;
        var phone = phones.FirstOrDefault(ph => Graph.Str(ph["type"]) == "mobile") ?? phones.FirstOrDefault(ph => Graph.Str(ph["type"]) == "business");
        return new Person(Graph.Str(p["id"]), Graph.Str(p["displayName"]), email.Length > 0 ? email : upn, upn, Graph.Str(p["jobTitle"]), Graph.Str(p["department"]), Graph.Str(p["officeLocation"]), Graph.Str(phone?["number"]));
    }

    private static Person FromUser(JsonNode u)
    {
        var upn = Graph.Str(u["userPrincipalName"]) is { Length: > 0 } x ? x : Graph.Str(u["mail"]);
        var email = Graph.Str(u["mail"]) is { Length: > 0 } mail ? mail : upn;
        return new Person(Graph.Str(u["id"]), Graph.Str(u["displayName"]), email, upn, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    [GeneratedRegex(@"^https://teams\.(microsoft|cloud\.microsoft)(\.com)?/")]
    private static partial Regex TeamsHost();

    [GeneratedRegex(@"https://teams\.microsoft\.com/l/meetup-join/\S+")]
    private static partial Regex MeetupLink();
}
