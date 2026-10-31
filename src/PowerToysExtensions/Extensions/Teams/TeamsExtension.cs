using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.Teams;

[Guid("611DC4FC-7EA8-44BA-8712-FCB418060C97")]
public sealed partial class TeamsExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new TeamsProvider();
}

public sealed partial class TeamsProvider : CommandProvider
{
    public const string ProviderId = "delido.teams";
    private readonly ICommandItem[] _commands;

    public TeamsProvider()
    {
        var api = new TeamsApi(new TeamsSettings());
        Id = ProviderId;
        DisplayName = "Microsoft Teams";
        Icon = Glyphs.Teams;
        Settings = api.Settings.Settings;
        _commands =
        [
            new CommandItem(new PeoplePage(api)) { Title = "Search People", Subtitle = "Teams" },
            new CommandItem(new ChatsPage(api)) { Title = "Search Chats", Subtitle = "Teams" },
            new CommandItem(new ChannelsPage(api)) { Title = "Teams & Channels", Subtitle = "Teams" },
            new CommandItem(new MessagesPage(api)) { Title = "Search Messages", Subtitle = "Teams" },
            new CommandItem(new StatusPage(api)) { Title = "Set Teams Status", Subtitle = "Teams" },
            new CommandItem(new MeetingsPage(api)) { Title = "Upcoming Meetings", Subtitle = "Teams" },
            new CommandItem(new JoinNextMeetingCommand(api)) { Title = "Join Next Meeting", Subtitle = "Teams" },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem? GetCommandItem(string id) => _commands.FirstOrDefault(c => c.Command?.Id == id);
}

internal static class Glyphs
{
    public static readonly IconInfo Teams = Ui.Glyph("");
    public static readonly IconInfo Person = Ui.Glyph("");
    public static readonly IconInfo Group = Ui.Glyph("");
    public static readonly IconInfo Chat = Ui.Glyph("");
    public static readonly IconInfo Channel = Ui.Glyph("");
    public static readonly IconInfo Lock = Ui.Glyph("");
    public static readonly IconInfo Calendar = Ui.Glyph("");
    public static readonly IconInfo Video = Ui.Glyph("");
    public static readonly IconInfo Status = Ui.Glyph("");
}

internal static class Shared
{
    public static ICommandItem? NotConfigured(TeamsApi api) =>
        api.Configured ? null : Ui.SetupItem(TeamsProvider.ProviderId, "Microsoft Teams (Client ID of the app registration)");

    /// <summary>Form to send a message; <paramref name="send"/> does the Graph call.</summary>
    public static CommandContextItem SendMessage(string to, Func<string, Task> send, string title = "Send Message")
    {
        var form = new FormBuilder()
            .Text($"To: {to}", bold: true)
            .Input("message", "Message", placeholder: "Type your message …", multiline: true, required: true)
            .Build(title);
        return new CommandContextItem(new FormPage(title, form, inputs =>
        {
            var text = inputs.Str("message");
            if (text.Length > 0)
            {
                Status.Start("Sending …", "Message sent", () => send(text));
            }

            return CommandResult.GoBack();
        }, Ui.Glyph("")))
        { Title = title, RequestedShortcut = Ui.Ctrl(VirtualKey.M) };
    }

    public static OpenUrlCommand Open(string url, string name) => new(url) { Name = name, Result = CommandResult.Dismiss() };
}

internal sealed partial class PeoplePage : SearchListPage
{
    private readonly TeamsApi _api;

    public PeoplePage(TeamsApi api)
    {
        _api = api;
        Id = "teams.search-people";
        Name = "Search";
        Title = "Search People";
        Icon = Glyphs.Person;
        PlaceholderText = "Search people by name or email …";
        ShowDetails = true;
    }

    protected override async Task<Batch> Search(string query, int page, CancellationToken token)
    {
        if (!_api.Configured)
        {
            return new([]);
        }

        var people = await _api.SearchPeople(query, token);
        var presences = await _api.Presences(people.Select(p => p.Id), token);
        var section = query.Length > 0 ? "People" : "Frequent Contacts";
        return new([.. people.Select(p => Item(p, TeamsApi.Presence(presences.GetValueOrDefault(p.Id)), section))]);
    }

    protected override ICommandItem? Empty(string query) => Shared.NotConfigured(_api) ?? Ui.EmptyItem("No people found", glyph: "");

    private ListItem Item(Person p, PresenceInfo? presence, string section)
    {
        var metadata = new List<IDetailsElement> { Ui.Label("Name", p.Name) };
        if (presence is not null)
        {
            metadata.Add(Ui.Tags("Status", presence.Tag));
        }

        foreach (var (key, value) in new[] { ("Job Title", p.JobTitle), ("Department", p.Department), ("Office", p.Office) })
        {
            if (value.Length > 0)
            {
                metadata.Add(Ui.Label(key, value));
            }
        }

        metadata.Add(Ui.Separator());
        metadata.Add(Ui.Link("Email", $"mailto:{p.Email}", p.Email));
        if (p.Phone.Length > 0)
        {
            metadata.Add(Ui.Link("Phone", $"tel:{p.Phone}", p.Phone));
        }

        var commands = new List<IContextItem>
        {
            Shared.SendMessage(p.Name, async text => await _api.SendChatMessage(await _api.OneOnOneChat(p.Id), text)),
            Ui.OpenUrl(_api.CallLink(p.Upn, video: false), "Call", Ui.CtrlShift(VirtualKey.A)),
            Ui.OpenUrl(_api.CallLink(p.Upn, video: true), "Video Call", Ui.CtrlShift(VirtualKey.V)),
            Ui.OpenUrl($"mailto:{p.Email}", "Send Email", Ui.Ctrl(VirtualKey.E)),
            Ui.Copy(p.Email, "Copy Email", Ui.CtrlShift(VirtualKey.C)),
            Ui.Copy(p.Name, "Copy Name", Ui.CtrlShift((VirtualKey)0xBE)),
        };
        if (p.Phone.Length > 0)
        {
            commands.Add(Ui.Copy(p.Phone, "Copy Phone Number"));
        }

        commands.Add(_api.Graph.SignOutAction(Reload));
        return new ListItem(Shared.Open(_api.ChatWithUserLink(p.Upn), "Chat in Teams"))
        {
            Title = p.Name,
            Subtitle = string.Join(" · ", new[] { p.JobTitle, p.Department }.Where(s => s.Length > 0)),
            Icon = Glyphs.Person,
            Section = section,
            Tags = presence is null ? [] : [presence.Tag],
            Details = new Details { Title = p.Name, Metadata = [.. metadata], Size = ContentSize.Small },
            MoreCommands = [.. commands],
        };
    }
}

internal sealed partial class ChatsPage : AsyncListPage
{
    private readonly TeamsApi _api;

    public ChatsPage(TeamsApi api)
    {
        _api = api;
        Id = "teams.search-chats";
        Name = "Open";
        Title = "Search Chats";
        Icon = Glyphs.Chat;
        PlaceholderText = "Filter chats …";
        MaxAge = TimeSpan.FromSeconds(30);
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        if (!_api.Configured)
        {
            return new([]);
        }

        var myIdTask = _api.MyId(token);
        var chats = await _api.Chats(token);
        var myId = await myIdTask;
        return new([.. chats.Select(chat => Item(chat, myId))]);
    }

    protected override ICommandItem? Empty() => Shared.NotConfigured(_api) ?? Ui.EmptyItem("No chats found", glyph: "");

    private ListItem Item(JsonNode chat, string myId)
    {
        var members = (chat["members"] as JsonArray ?? []).OfType<JsonNode>().ToArray();
        var others = members.Where(m => Graph.Str(m["userId"]) != myId).Select(m => Graph.Str(m["displayName"])).Where(n => n.Length > 0).ToArray();
        var title = Graph.Str(chat["topic"]) is { Length: > 0 } topic ? topic : others.Length > 0 ? string.Join(", ", others) : "Chat";
        var preview = chat["lastMessagePreview"];
        var text = Graph.StripHtml(Graph.Str(preview?["body"]?["content"]));
        var from = Graph.Str(preview?["from"]?["user"]?["displayName"]);
        var when = Graph.Time(preview?["createdDateTime"]);
        var url = Graph.Str(chat["webUrl"]);
        var chatId = Graph.Str(chat["id"]);
        return new ListItem(Shared.Open(_api.Link(url), "Open in Teams"))
        {
            Title = title,
            Subtitle = text.Length > 0 ? (from.Length > 0 ? $"{from}: {text}" : text) : string.Empty,
            Icon = Graph.Str(chat["chatType"]) switch
            {
                "oneOnOne" => Glyphs.Person,
                "meeting" => Glyphs.Calendar,
                _ => Glyphs.Group,
            },
            Tags = when is null ? [] : [Ui.Tag(Ui.Ago(when))],
            MoreCommands =
            [
                Shared.SendMessage(title, text => _api.SendChatMessage(chatId, text)),
                Ui.Copy(url, "Copy Chat Link", Ui.CtrlShift(VirtualKey.C)),
                Ui.Action("Refresh", "", Reload, Ui.Ctrl(VirtualKey.R)),
                _api.Graph.SignOutAction(Reload),
            ],
        };
    }
}

internal sealed partial class ChannelsPage : AsyncListPage
{
    private readonly TeamsApi _api;

    public ChannelsPage(TeamsApi api)
    {
        _api = api;
        Id = "teams.teams-channels";
        Name = "Open";
        Title = "Teams & Channels";
        Icon = Glyphs.Channel;
        PlaceholderText = "Filter teams and channels …";
        MaxAge = TimeSpan.FromMinutes(10);
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        if (!_api.Configured)
        {
            return new([]);
        }

        var items = new List<IListItem>();
        foreach (var (team, channels) in await _api.TeamsWithChannels(token))
        {
            var teamName = Graph.Str(team["displayName"]);
            var teamId = Graph.Str(team["id"]);
            items.AddRange(channels.Select(channel =>
            {
                var name = Graph.Str(channel["displayName"]);
                var url = Graph.Str(channel["webUrl"]);
                var channelId = Graph.Str(channel["id"]);
                return new ListItem(Shared.Open(_api.Link(url), "Open in Teams"))
                {
                    Title = name,
                    Subtitle = Graph.Str(channel["description"]),
                    Icon = Graph.Str(channel["membershipType"]) == "private" ? Glyphs.Lock : Glyphs.Channel,
                    Section = teamName,
                    Tags = [Ui.Tag(teamName)],
                    MoreCommands =
                    [
                        Shared.SendMessage($"{teamName} › {name}", text => _api.SendChannelMessage(teamId, channelId, text), "Post Message"),
                        Ui.Copy(url, "Copy Channel Link", Ui.CtrlShift(VirtualKey.C)),
                        Ui.Action("Refresh", "", Reload, Ui.Ctrl(VirtualKey.R)),
                        _api.Graph.SignOutAction(Reload),
                    ],
                };
            }));
        }

        return new([.. items]);
    }

    protected override ICommandItem? Empty() => Shared.NotConfigured(_api) ?? Ui.EmptyItem("No teams found", glyph: "");
}

internal sealed partial class MessagesPage : SearchListPage
{
    private const int PageSize = 25;
    private readonly TeamsApi _api;

    public MessagesPage(TeamsApi api)
    {
        _api = api;
        Id = "teams.search-messages";
        Name = "Search";
        Title = "Search Messages";
        Icon = Ui.Glyph("");
        PlaceholderText = "Search chat and channel messages … supports KQL, e.g. from:\"Jane Doe\"";
        Debounce = TimeSpan.FromMilliseconds(400);
    }

    protected override async Task<Batch> Search(string query, int page, CancellationToken token)
    {
        if (query.Length == 0 || !_api.Configured)
        {
            return new([]);
        }

        var (hits, hasMore) = await _api.Graph.Search("chatMessage", Graph.PrefixQuery(query), page * PageSize, PageSize, token: token);
        return new([.. hits.OfType<JsonNode>().Select(Item)], hasMore);
    }

    protected override ICommandItem? Empty(string query) =>
        Shared.NotConfigured(_api) ?? (query.Length == 0
            ? Ui.EmptyItem("Type to search messages", "Supports KQL, e.g. from:\"Jane Doe\" or \"exact phrase\"", "")
            : Ui.EmptyItem("No messages found"));

    private static string? MessageLink(JsonNode r)
    {
        if (Graph.Str(r["webLink"]) is { Length: > 0 } link)
        {
            return link;
        }

        var id = Graph.Str(r["id"]);
        if (Graph.Str(r["chatId"]) is { Length: > 0 } chatId)
        {
            return $"https://teams.microsoft.com/l/message/{chatId}/{id}?context={Uri.EscapeDataString("{\"contextType\":\"chat\"}")}";
        }

        var channel = Graph.Str(r["channelIdentity"]?["channelId"]);
        var team = Graph.Str(r["channelIdentity"]?["teamId"]);
        return channel.Length > 0 && team.Length > 0 ? $"https://teams.microsoft.com/l/message/{channel}/{id}?groupId={team}" : null;
    }

    private ListItem Item(JsonNode hit)
    {
        var r = hit["resource"]!;
        var link = MessageLink(r);
        var summary = Graph.Summary(Graph.Str(hit["summary"])) is { Length: > 0 } s ? s : Graph.Str(r["subject"]) is { Length: > 0 } subject ? subject : "(no preview)";
        var when = Graph.Time(r["createdDateTime"]);
        var commands = new List<IContextItem>();
        if (link is not null)
        {
            commands.Add(Ui.Copy(link, "Copy Message Link", Ui.CtrlShift(VirtualKey.C)));
        }

        commands.Add(Ui.Copy(summary, "Copy Text", Ui.CtrlShift((VirtualKey)0xBE)));
        commands.Add(_api.Graph.SignOutAction(Reload));
        return new ListItem(link is null ? new NoOpCommand() : Shared.Open(_api.Link(link), "Open in Teams"))
        {
            Title = summary,
            Subtitle = Graph.Str(r["from"]?["emailAddress"]?["name"]),
            Icon = Graph.Str(r["channelIdentity"]?["channelId"]).Length > 0 ? Glyphs.Channel : Glyphs.Chat,
            Tags = when is null ? [] : [Ui.Tag(Ui.Ago(when))],
            MoreCommands = [.. commands],
        };
    }
}

internal sealed partial class StatusPage : AsyncListPage
{
    private static readonly (string Title, string Availability, string Activity, Func<string, Tag> Color)[] Statuses =
    [
        ("Available", "Available", "Available", Ui.Green),
        ("Busy", "Busy", "Busy", Ui.Red),
        ("Do Not Disturb", "DoNotDisturb", "DoNotDisturb", Ui.Red),
        ("Be Right Back", "BeRightBack", "BeRightBack", Ui.Yellow),
        ("Appear Away", "Away", "Away", Ui.Yellow),
        ("Appear Offline", "Offline", "OffWork", Ui.Gray),
    ];

    private static readonly (string Title, string Value)[] Durations =
    [
        ("30 minutes", "PT30M"),
        ("1 hour", "PT1H"),
        ("2 hours", "PT2H"),
        ("4 hours", "PT4H"),
        ("today", "endOfDay"),
    ];

    private readonly TeamsApi _api;

    public StatusPage(TeamsApi api)
    {
        _api = api;
        Id = "teams.set-status";
        Name = "Open";
        Title = "Set Teams Status";
        Icon = Glyphs.Status;
        PlaceholderText = "Set your Teams status …";
        MaxAge = TimeSpan.FromSeconds(5);
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        if (!_api.Configured)
        {
            return new([]);
        }

        var items = new List<IListItem>();
        string? message = null;
        try
        {
            var presence = await _api.MyPresence(token);
            message = Graph.StripHtml(Graph.Str(presence["statusMessage"]?["message"]?["content"]));
            var info = TeamsApi.Presence(presence);
            items.Add(new ListItem(MessagePage(message))
            {
                Title = info?.Label ?? Graph.Str(presence["availability"]),
                Subtitle = message.Length > 0 ? $"“{message}”" : "No status message",
                Icon = Glyphs.Status,
                Section = "Current",
                Tags = info is null ? [] : [info.Tag],
                MoreCommands = [Ui.Action("Reset Status", "", Reset), _api.Graph.SignOutAction(Reload)],
            });
        }
        catch (Exception ex) when (ex is ApiException or InvalidOperationException)
        {
            // setting the status works without reading it; show the error as the first entry
            items.Add(new ListItem(new AnonymousCommand(Reload) { Name = "Try Again", Result = CommandResult.KeepOpen() }) { Title = "Current status unavailable", Subtitle = ex.Message, Icon = Ui.Glyph(""), Section = "Current" });
        }

        foreach (var status in Statuses)
        {
            items.Add(new ListItem(new AnonymousCommand(() => Set(status.Title, status.Availability, status.Activity, null)) { Name = $"Set {status.Title}", Result = CommandResult.Dismiss() })
            {
                Title = status.Title,
                Icon = Glyphs.Status,
                Section = "Set Status",
                Tags = [status.Color("●")],
                MoreCommands = [.. Durations.Select(d => (IContextItem)Ui.Action($"{status.Title} for {d.Title}", "", () => Set(status.Title, status.Availability, status.Activity, d.Value), result: CommandResult.Dismiss()))],
            });
        }

        items.Add(new ListItem(new AnonymousCommand(Reset) { Name = "Reset Status", Result = CommandResult.Dismiss() }) { Title = "Reset Status", Subtitle = "Back to automatic status", Icon = Ui.Glyph(""), Section = "Set Status" });
        items.Add(new ListItem(MessagePage(message)) { Title = "Set Status Message", Icon = Glyphs.Chat, Section = "Set Status" });
        return new([.. items]);
    }

    protected override ICommandItem? Empty() => Shared.NotConfigured(_api);

    private static string? IsoDuration(string? value)
    {
        if (value != "endOfDay")
        {
            return value;
        }

        var minutes = Math.Max(1, (int)Math.Round((DateTime.Today.AddHours(23).AddMinutes(59) - DateTime.Now).TotalMinutes));
        return $"PT{minutes}M";
    }

    private void Set(string title, string availability, string activity, string? duration) =>
        Status.Start($"Setting {title} …", $"Teams status: {title}", () => _api.SetPresence(availability, activity, IsoDuration(duration)), Reload);

    private void Reset() => Status.Start("Resetting status …", "Teams status reset", _api.ResetPresence, Reload);

    private FormPage MessagePage(string? current)
    {
        var form = new FormBuilder()
            .Input("message", "Message", current ?? string.Empty, "Leave empty to clear", multiline: true)
            .Choice("expiry", "Clear after", [("Never", "0"), ("1 hour", "1"), ("4 hours", "4"), ("Today", Math.Max(1, 24 - DateTime.Now.Hour).ToString(System.Globalization.CultureInfo.InvariantCulture)), ("This week", "168")], "0")
            .Build("Save Status Message");
        return new FormPage("Status Message", form, inputs =>
        {
            var message = inputs.Str("message");
            var hours = int.TryParse(inputs.Str("expiry"), out var h) ? h : 0;
            Status.Start("Saving …", message.Length > 0 ? "Status message set" : "Status message cleared", () => _api.SetStatusMessage(message, hours), Reload);
            return CommandResult.GoBack();
        }, Glyphs.Chat);
    }
}

internal sealed partial class MeetingsPage : AsyncListPage
{
    private readonly TeamsApi _api;

    public MeetingsPage(TeamsApi api)
    {
        _api = api;
        Id = "teams.meetings";
        Name = "Open";
        Title = "Upcoming Meetings";
        Icon = Glyphs.Calendar;
        PlaceholderText = "Filter meetings …";
        MaxAge = TimeSpan.FromMinutes(1);
    }

    protected override async Task<Batch> Load(int page, CancellationToken token)
    {
        if (!_api.Configured)
        {
            return new([]);
        }

        var now = DateTimeOffset.UtcNow;
        return new([.. (await _api.UpcomingMeetings(7, token)).Select(m =>
        {
            var running = m.Start <= now && m.End > now;
            var soon = !running && m.Start - now < TimeSpan.FromMinutes(15);
            var tags = new List<ITag>();
            if (running)
            {
                tags.Add(Ui.Green("Now"));
            }
            else if (soon)
            {
                tags.Add(Ui.Orange("Soon"));
            }

            tags.Add(Ui.Tag(m.AllDay ? "All day" : $"{m.Start.ToLocalTime():t} – {m.End.ToLocalTime():t}"));
            var commands = new List<IContextItem>();
            if (m.WebLink.Length > 0)
            {
                commands.Add(Ui.OpenUrl(m.WebLink, "Open in Outlook", Ui.Ctrl(VirtualKey.O)));
            }

            if (m.JoinUrl is { } join)
            {
                commands.Add(Ui.Copy(join, "Copy Join Link", Ui.CtrlShift(VirtualKey.C)));
            }

            commands.Add(Ui.Action("Refresh", "", Reload, Ui.Ctrl(VirtualKey.R)));
            commands.Add(_api.Graph.SignOutAction(Reload));
            return (IListItem)new ListItem(m.JoinUrl is { } url ? Shared.Open(_api.Link(url), "Join Meeting") : Shared.Open(m.WebLink, "Open in Outlook"))
            {
                Title = m.Subject,
                Subtitle = m.Organizer,
                Icon = m.JoinUrl is null ? Glyphs.Calendar : Glyphs.Video,
                Section = DayLabel(m.Start.ToLocalTime()),
                Tags = [.. tags],
                MoreCommands = [.. commands],
            };
        })]);
    }

    protected override ICommandItem? Empty() => Shared.NotConfigured(_api) ?? Ui.EmptyItem("No upcoming meetings", glyph: "");

    private static string DayLabel(DateTimeOffset date) =>
        date.Date == DateTime.Today ? "Today" : date.Date == DateTime.Today.AddDays(1) ? "Tomorrow" : date.ToString("dddd, d. MMMM", System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>Joins the running meeting, or the next one starting within 30 minutes.</summary>
internal sealed partial class JoinNextMeetingCommand : InvokableCommand
{
    private readonly TeamsApi _api;

    public JoinNextMeetingCommand(TeamsApi api)
    {
        _api = api;
        Id = "teams.join-next-meeting";
        Name = "Join";
        Icon = Glyphs.Video;
    }

    public override ICommandResult Invoke()
    {
        if (!_api.Configured)
        {
            return CommandResult.ShowToast(new ToastArgs { Message = "Enter the Client ID in the settings of Microsoft Teams first", Result = CommandResult.KeepOpen() });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var meetings = (await _api.UpcomingMeetings(1)).Where(m => !m.AllDay && m.JoinUrl is not null).ToArray();
                var candidate = meetings.FirstOrDefault(m => m.Start - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(30));
                if (candidate is null)
                {
                    Status.Info(meetings.FirstOrDefault() is { } next
                        ? $"No meeting right now – next: {next.Subject} at {next.Start.ToLocalTime():t}"
                        : "No Teams meetings in the next 24 hours");
                    return;
                }

                ShellHelpers.OpenInShell(_api.Link(candidate.JoinUrl!));
                Status.Info($"Joining: {candidate.Subject}");
            }
            catch (Exception ex)
            {
                Status.Error($"Could not join meeting: {ex.Message}");
            }
        });
        return CommandResult.KeepOpen();
    }
}
