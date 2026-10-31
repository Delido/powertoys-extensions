using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;
using Windows.System;

namespace PowerToysExtensions.Extensions.OneDev;

[Guid("9AEC9729-A270-40D2-BA86-780721AA64AD")]
public sealed partial class OneDevExtension : ExtensionBase
{
    protected override CommandProvider Provider { get; } = new OneDevProvider();
}

public sealed class OneDevSettings : SettingsBase
{
    private readonly TextSetting _url = new("serverUrl", "Server URL", "Base URL of your OneDev server", string.Empty) { Placeholder = "https://onedev.example.com" };
    private readonly SecretSetting _token = new("accessToken", "Access Token", "Create one in OneDev under My Account → Access Tokens (stored encrypted)");

    public OneDevSettings()
        : base("onedev")
    {
        Settings.Add(_url);
        Settings.Add(_token);
        Load();
    }

    public string ServerUrl => Http.TrimUrl(_url.Value ?? string.Empty);

    public bool Configured => ServerUrl.Length > 0 && _token.HasValue;

    public AuthenticationHeaderValue Auth => new("Bearer", _token.Value);
}

public sealed record Project(int Id, string Name, string? Key, string Path, string? Description, DateTimeOffset CreateDate, bool CodeManagement);

public sealed record Activity(DateTimeOffset Date, string Description);

public sealed record Issue(int Id, int Number, int ProjectId, string Title, string? Description, string State, DateTimeOffset SubmitDate, int CommentCount, int VoteCount, bool Confidential, Activity? LastActivity);

public sealed record PullRequest(int Id, int Number, int TargetProjectId, string Title, string? Description, string Status, bool WorkInProgress, DateTimeOffset SubmitDate, string? SourceBranch, string? TargetBranch, Activity? LastActivity);

public sealed record Build(int Id, int Number, int ProjectId, string JobName, string? RefName, string? Version, string CommitHash, string Status, DateTimeOffset SubmitDate, DateTimeOffset? FinishDate);

public sealed class OneDevApi(OneDevSettings settings)
{
    public const int PageSize = 50;
    private readonly ConcurrentDictionary<int, string> _paths = new();

    public OneDevSettings Settings => settings;

    public string ServerUrl => settings.ServerUrl;

    public Task<T> Get<T>(string path, string query, CancellationToken token)
    {
        if (!settings.Configured)
        {
            throw new InvalidOperationException("OneDev is not set up yet – open the settings of this extension");
        }

        return Http.GetJson<T>(Http.Client(), Http.Request(HttpMethod.Get, $"{ServerUrl}/~api/{path}?{query}", settings.Auth), token);
    }

    /// <summary>Runs a paged OneDev query and resolves the project paths needed for web links.</summary>
    public async Task<(T[] Items, Dictionary<int, string> Paths)> Query<T>(string endpoint, string query, int page, Func<T, int> projectId, CancellationToken token)
    {
        var items = await Get<T[]>(endpoint, Http.Query(("query", query), ("offset", (page * PageSize).ToString()), ("count", PageSize.ToString())), token);
        var ids = items.Select(projectId).Distinct().Where(id => !_paths.ContainsKey(id)).ToArray();
        await Task.WhenAll(ids.Select(async id =>
        {
            try
            {
                _paths[id] = (await Get<Project>($"projects/{id}", string.Empty, token)).Path;
            }
            catch (ApiException)
            {
                // item falls back to the server URL
            }
        }));
        return (items, items.Select(projectId).Distinct().Where(_paths.ContainsKey).ToDictionary(id => id, id => _paths[id]));
    }

    /// <summary>Combines a base filter, the fuzzy search text and an order clause into a OneDev query.</summary>
    public static string BuildQuery(string filter, string text, string order)
    {
        var parts = new List<string>();
        if (filter.Length > 0)
        {
            parts.Add(filter);
        }

        text = text.Replace("~", string.Empty, StringComparison.Ordinal).Trim();
        if (text.Length > 0)
        {
            parts.Add($"~{text}~");
        }

        return $"{string.Join(" and ", parts)} {order}".Trim();
    }

    public string ProjectUrl(string path) => $"{ServerUrl}/{path}";

    public string ItemUrl(string? path, string kind, int number) => path is null ? ServerUrl : $"{ServerUrl}/{path}/~{kind}/{number}";

    /// <summary>Makes relative links (attachments) absolute so they render.</summary>
    public string Absolutize(string? markdown) =>
        Regex.Replace(string.IsNullOrWhiteSpace(markdown) ? "_No description_" : markdown, @"\]\((/[^)]+)\)", m => $"]({ServerUrl}{m.Groups[1].Value})");
}

public sealed partial class OneDevProvider : CommandProvider
{
    public const string ProviderId = "delido.onedev";
    private readonly ICommandItem[] _commands;

    public OneDevProvider()
    {
        var api = new OneDevApi(new OneDevSettings());
        Id = ProviderId;
        DisplayName = "OneDev";
        Icon = Ui.Glyph("");
        Settings = api.Settings.Settings;
        _commands =
        [
            new CommandItem(new ProjectsPage(api)) { Title = "Search Projects", Subtitle = "OneDev" },
            new CommandItem(new IssuesPage(api, null)) { Title = "Search Issues", Subtitle = "OneDev" },
            new CommandItem(new PullRequestsPage(api, null)) { Title = "Search Pull Requests", Subtitle = "OneDev" },
            new CommandItem(new BuildsPage(api, null)) { Title = "Search Builds", Subtitle = "OneDev" },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem? GetCommandItem(string id) => _commands.FirstOrDefault(c => c.Command?.Id == id);
}

/// <summary>Search page over one OneDev entity with a filter dropdown, optionally scoped to a project.</summary>
internal abstract partial class OneDevQueryPage : SearchListPage
{
    protected OneDevQueryPage(OneDevApi api, string? projectPath, string id, string title, IconInfo icon, ChoiceFilters filters)
    {
        Api = api;
        ProjectPath = projectPath;
        Id = projectPath is null ? id : string.Empty;
        Name = projectPath is null ? "Open" : $"Show {title}";
        Title = projectPath is null ? title : $"{title} · {projectPath}";
        Icon = icon;
        PlaceholderText = projectPath is null ? $"Search {title.ToLowerInvariant()} …" : $"Search {title.ToLowerInvariant()} in {projectPath} …";
        ShowDetails = true;
        Filters = filters;
        filters.Changed += Reload;
    }

    protected OneDevApi Api { get; }

    protected string? ProjectPath { get; }

    protected string Filter => (Filters as ChoiceFilters)?.CurrentFilterId ?? string.Empty;

    protected string Scope(string field, string filter) =>
        string.Join(" and ", new[] { ProjectPath is null ? string.Empty : $"\"{field}\" is \"{ProjectPath}\"", filter }.Where(s => s.Length > 0));

    protected override ICommandItem? Empty(string query) =>
        Api.Settings.Configured ? Ui.EmptyItem("Nothing found", string.Empty, "") : Ui.SetupItem(OneDevProvider.ProviderId, "OneDev");

    protected static IContextItem[] Copies(string url, string reference, string title) =>
    [
        Ui.Copy(url, "Copy URL", Ui.CtrlShift(VirtualKey.C)),
        Ui.Copy(reference, "Copy Reference", Ui.CtrlShift((VirtualKey)0xBE)),
        Ui.Copy(title, "Copy Title"),
    ];
}

internal sealed partial class ProjectsPage : SearchListPage
{
    private readonly OneDevApi _api;

    public ProjectsPage(OneDevApi api)
    {
        _api = api;
        Id = "search-projects";
        Name = "Open";
        Title = "OneDev Projects";
        Icon = Ui.Glyph("");
        PlaceholderText = "Search projects …";
    }

    protected override async Task<Batch> Search(string query, int page, CancellationToken token)
    {
        var q = OneDevApi.BuildQuery(string.Empty, query, "order by \"Last Activity Date\" desc");
        var projects = await _api.Get<Project[]>("projects", Http.Query(("query", q), ("offset", (page * OneDevApi.PageSize).ToString()), ("count", OneDevApi.PageSize.ToString())), token);
        return new([.. projects.Select(Item)], projects.Length == OneDevApi.PageSize);
    }

    protected override ICommandItem? Empty(string query) =>
        _api.Settings.Configured ? Ui.EmptyItem("No projects found") : Ui.SetupItem(OneDevProvider.ProviderId, "OneDev");

    private ListItem Item(Project p)
    {
        var url = _api.ProjectUrl(p.Path);
        return new ListItem(new OpenUrlCommand(url) { Name = "Open in Browser" })
        {
            Title = p.Path,
            Subtitle = p.Description ?? string.Empty,
            Icon = Ui.Glyph(p.CodeManagement ? "" : ""),
            Tags = p.Key is null ? [] : [Ui.Tag(p.Key)],
            MoreCommands =
            [
                new CommandContextItem(new IssuesPage(_api, p.Path)) { Title = "Show Issues", RequestedShortcut = Ui.Ctrl(VirtualKey.I) },
                new CommandContextItem(new PullRequestsPage(_api, p.Path)) { Title = "Show Pull Requests", RequestedShortcut = Ui.Ctrl(VirtualKey.R) },
                new CommandContextItem(new BuildsPage(_api, p.Path)) { Title = "Show Builds", RequestedShortcut = Ui.Ctrl(VirtualKey.B) },
                Ui.Copy(url, "Copy URL", Ui.CtrlShift(VirtualKey.C)),
                Ui.Copy($"{url}.git", "Copy Clone URL", Ui.CtrlShift((VirtualKey)0xBE)),
                Ui.Copy(p.Path, "Copy Path"),
            ],
        };
    }
}

internal sealed partial class IssuesPage(OneDevApi api, string? projectPath)
    : OneDevQueryPage(api, projectPath, "search-issues", "Issues", Ui.Glyph(""), new ChoiceFilters(
        ("\"State\" is \"Open\"", "Open"),
        ("\"Assignee\" is me", "Assigned to Me"),
        ("submitted by me", "Submitted by Me"),
        ("watched by me", "Watched by Me"),
        ("mentioned me", "Mentioned Me"),
        (string.Empty, "All")))
{
    protected override async Task<Batch> Search(string query, int page, CancellationToken token)
    {
        var q = OneDevApi.BuildQuery(Scope("Project", Filter), query, "order by \"Last Activity Date\" desc");
        var (issues, paths) = await Api.Query<Issue>("issues", q, page, i => i.ProjectId, token);
        return new([.. issues.Select(i => Item(i, paths.GetValueOrDefault(i.ProjectId)))], issues.Length == OneDevApi.PageSize);
    }

    private static Tag StateTag(string state) => state.ToLowerInvariant() switch
    {
        "open" => Ui.Green(state),
        "closed" or "released" => Ui.Purple(state),
        var s when s.Contains("progress", StringComparison.Ordinal) => Ui.Orange(state),
        _ => Ui.Blue(state),
    };

    private ListItem Item(Issue issue, string? path)
    {
        var url = Api.ItemUrl(path, "issues", issue.Number);
        var reference = $"{path}#{issue.Number}";
        var activity = issue.LastActivity?.Date ?? issue.SubmitDate;
        var tags = new List<ITag>();
        if (issue.Confidential)
        {
            tags.Add(Ui.Gray("Confidential"));
        }

        if (issue.CommentCount > 0)
        {
            tags.Add(Ui.Tag($"💬 {issue.CommentCount}"));
        }

        tags.Add(StateTag(issue.State));
        tags.Add(Ui.Tag(Ui.Ago(activity)));

        return new ListItem(new OpenUrlCommand(url) { Name = "Open in Browser" })
        {
            Title = issue.Title,
            Subtitle = reference,
            Icon = Ui.Glyph(""),
            Tags = [.. tags],
            Details = new Details
            {
                Title = issue.Title,
                Body = Api.Absolutize(issue.Description),
                Metadata =
                [
                    Ui.Label("Project", path),
                    Ui.Label("Number", $"#{issue.Number}"),
                    Ui.Tags("State", StateTag(issue.State)),
                    Ui.Label("Submitted", Ui.Date(issue.SubmitDate)),
                    Ui.Label("Last Activity", issue.LastActivity is { } a ? $"{a.Description} · {Ui.Date(a.Date)}" : null),
                    Ui.Label("Comments", issue.CommentCount.ToString()),
                    Ui.Label("Votes", issue.VoteCount.ToString()),
                ],
            },
            MoreCommands = Copies(url, reference, issue.Title),
        };
    }
}

internal sealed partial class PullRequestsPage(OneDevApi api, string? projectPath)
    : OneDevQueryPage(api, projectPath, "search-pull-requests", "Pull Requests", Ui.Glyph(""), new ChoiceFilters(
        ("open", "Open"),
        ("to be reviewed by me", "To Be Reviewed by Me"),
        ("submitted by me", "Submitted by Me"),
        ("assigned to me", "Assigned to Me"),
        ("merged", "Merged"),
        (string.Empty, "All")))
{
    protected override async Task<Batch> Search(string query, int page, CancellationToken token)
    {
        var q = OneDevApi.BuildQuery(Scope("Target Project", Filter), query, "order by \"Last Activity Date\" desc");
        var (pulls, paths) = await Api.Query<PullRequest>("pulls", q, page, p => p.TargetProjectId, token);
        return new([.. pulls.Select(p => Item(p, paths.GetValueOrDefault(p.TargetProjectId)))], pulls.Length == OneDevApi.PageSize);
    }

    private static Tag StatusTag(string status) => status switch
    {
        "MERGED" => Ui.Purple("Merged"),
        "DISCARDED" => Ui.Red("Discarded"),
        _ => Ui.Green("Open"),
    };

    private ListItem Item(PullRequest pr, string? path)
    {
        var url = Api.ItemUrl(path, "pulls", pr.Number);
        var reference = $"{path}#{pr.Number}";
        var branches = pr.SourceBranch is not null && pr.TargetBranch is not null ? $"{pr.SourceBranch} → {pr.TargetBranch}" : null;
        var tags = new List<ITag>();
        if (pr.WorkInProgress)
        {
            tags.Add(Ui.Yellow("WIP"));
        }

        if (branches is not null)
        {
            tags.Add(Ui.Tag(branches));
        }

        tags.Add(StatusTag(pr.Status));
        tags.Add(Ui.Tag(Ui.Ago(pr.LastActivity?.Date ?? pr.SubmitDate)));

        var commands = Copies(url, reference, pr.Title).ToList();
        if (pr.SourceBranch is not null)
        {
            commands.Add(Ui.Copy(pr.SourceBranch, "Copy Source Branch"));
        }

        return new ListItem(new OpenUrlCommand(url) { Name = "Open in Browser" })
        {
            Title = pr.Title,
            Subtitle = reference,
            Icon = Ui.Glyph(""),
            Tags = [.. tags],
            Details = new Details
            {
                Title = pr.Title,
                Body = Api.Absolutize(pr.Description),
                Metadata =
                [
                    Ui.Label("Project", path),
                    Ui.Label("Number", $"#{pr.Number}"),
                    Ui.Tags("Status", pr.WorkInProgress ? [StatusTag(pr.Status), Ui.Yellow("WIP")] : [StatusTag(pr.Status)]),
                    Ui.Label("Source Branch", pr.SourceBranch),
                    Ui.Label("Target Branch", pr.TargetBranch),
                    Ui.Label("Submitted", Ui.Date(pr.SubmitDate)),
                    Ui.Label("Last Activity", pr.LastActivity is { } a ? $"{a.Description} · {Ui.Date(a.Date)}" : null),
                ],
            },
            MoreCommands = [.. commands],
        };
    }
}

internal sealed partial class BuildsPage(OneDevApi api, string? projectPath)
    : OneDevQueryPage(api, projectPath, "search-builds", "Builds", Ui.Glyph(""), new ChoiceFilters(
        (string.Empty, "All"),
        ("running", "Running"),
        ("failed", "Failed"),
        ("successful", "Successful"),
        ("submitted by me", "Submitted by Me")))
{
    protected override async Task<Batch> Search(string query, int page, CancellationToken token)
    {
        var q = OneDevApi.BuildQuery(Scope("Project", Filter), query, "order by \"Submit Date\" desc");
        var (builds, paths) = await Api.Query<Build>("builds", q, page, b => b.ProjectId, token);
        return new([.. builds.Select(b => Item(b, paths.GetValueOrDefault(b.ProjectId)))], builds.Length == OneDevApi.PageSize);
    }

    private static (Tag Tag, string Glyph) Status(string status) => status switch
    {
        "PENDING" => (Ui.Yellow("Pending"), ""),
        "RUNNING" => (Ui.Blue("Running"), ""),
        "SUCCESSFUL" => (Ui.Green("Successful"), ""),
        "FAILED" => (Ui.Red("Failed"), ""),
        "CANCELLED" => (Ui.Gray("Cancelled"), ""),
        "TIMED_OUT" => (Ui.Orange("Timed Out"), ""),
        _ => (Ui.Gray("Waiting"), ""),
    };

    private static string? ShortRef(string? reference) =>
        reference is null ? null : Regex.Replace(Regex.Replace(reference, "^refs/(heads|tags)/", string.Empty), @"^refs/pull/(\d+)/.*", "PR #$1");

    private ListItem Item(Build build, string? path)
    {
        var url = Api.ItemUrl(path, "builds", build.Number);
        var (tag, glyph) = Status(build.Status);
        var reference = ShortRef(build.RefName);
        var tags = new List<ITag>();
        if (reference is not null)
        {
            tags.Add(Ui.Tag(reference));
        }

        tags.Add(tag);
        tags.Add(Ui.Tag(Ui.Ago(build.FinishDate ?? build.SubmitDate)));

        return new ListItem(new OpenUrlCommand(url) { Name = "Open in Browser" })
        {
            Title = build.Version is null ? build.JobName : $"{build.JobName} ({build.Version})",
            Subtitle = $"{path}#{build.Number}",
            Icon = Ui.Glyph(glyph),
            Tags = [.. tags],
            TextToSuggest = build.Number.ToString(),
            MoreCommands =
            [
                Ui.OpenUrl($"{url}/log", "Open Log", Ui.Ctrl(VirtualKey.L)),
                Ui.Copy(url, "Copy URL", Ui.CtrlShift(VirtualKey.C)),
                Ui.Copy(build.CommitHash, "Copy Commit Hash", Ui.CtrlShift((VirtualKey)0xBE)),
            ],
        };
    }
}
