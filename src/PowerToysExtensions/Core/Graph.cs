using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerToysExtensions.Core;

/// <summary>Microsoft Graph calls with a token from <see cref="MicrosoftAuth"/>.</summary>
public sealed partial class Graph(MicrosoftAuth auth)
{
    private static readonly HashSet<string> KqlOperators = ["AND", "OR", "NOT", "NEAR", "ONEAR"];

    public MicrosoftAuth Auth => auth;

    public Task<JsonNode> Get(string path, CancellationToken token = default, params (string Name, string Value)[] headers) =>
        Send(HttpMethod.Get, path, null, token, headers);

    public Task<JsonNode> Post(string path, JsonNode body, CancellationToken token = default) =>
        Send(HttpMethod.Post, path, body, token);

    /// <summary>Runs one Microsoft Search request (KQL) and returns the hits and whether more exist.</summary>
    public async Task<(JsonArray Hits, bool HasMore)> Search(string entityType, string query, int from, int size, string[]? fields = null, CancellationToken token = default)
    {
        var request = new JsonObject
        {
            ["entityTypes"] = new JsonArray(entityType),
            ["query"] = new JsonObject { ["queryString"] = query },
            ["from"] = from,
            ["size"] = size,
        };
        if (fields is not null)
        {
            request["fields"] = new JsonArray([.. fields.Select(f => (JsonNode)f)]);
        }

        var result = await Post("search/query", new JsonObject { ["requests"] = new JsonArray(request) }, token);
        var container = result["value"]?[0]?["hitsContainers"]?[0];
        return (container?["hits"] as JsonArray ?? [], container?["moreResultsAvailable"]?.GetValue<bool>() ?? false);
    }

    /// <summary>Sign-out for the actions of every item.</summary>
    public CommandContextItem SignOutAction(Action? after = null) =>
        Ui.Action("Sign out of Microsoft", "", () => Status.Start("Signing out …", "Signed out", auth.SignOut, after));

    /// <summary>
    /// Microsoft Search only matches whole words: "*" is appended to plain words so that "Troublesh" already
    /// finds "Troubleshooting". KQL syntax (quotes, property:value, operators, parentheses) stays as it is.
    /// </summary>
    public static string PrefixQuery(string text) =>
        text.Contains('"', StringComparison.Ordinal)
            ? text
            : string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w =>
                KqlOperators.Contains(w) || w.IndexOfAny([':', '*', '(', ')', '=', '<', '>']) >= 0 || w.StartsWith('-') ? w : $"{w}*"));

    /// <summary>Converts search highlight markup (&lt;c0&gt;, &lt;ddd/&gt;) to plain text or markdown.</summary>
    public static string Summary(string? summary, bool markdown = false) =>
        string.IsNullOrEmpty(summary) ? string.Empty : StripHtml(Highlight().Replace(summary, markdown ? "**$1**" : "$1").Replace("<ddd/>", "…", StringComparison.Ordinal));

    public static string StripHtml(string? html) =>
        string.IsNullOrEmpty(html) ? string.Empty : Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(html, " ")), " ").Trim();

    public static string Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : string.Empty;

    public static DateTimeOffset? Time(JsonNode? node) => DateTimeOffset.TryParse(Str(node), out var t) ? t : null;

    private async Task<JsonNode> Send(HttpMethod method, string path, JsonNode? body, CancellationToken token, params (string Name, string Value)[] headers)
    {
        var url = path.StartsWith("https://", StringComparison.Ordinal) ? path : $"https://graph.microsoft.com/v1.0/{path}";
        var request = Http.Request(method, url, new AuthenticationHeaderValue("Bearer", await auth.Token(token)));
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await Http.Send(Http.Client(), request, token);
            var text = await response.Content.ReadAsStringAsync(token);
            return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text) ?? new JsonObject();
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.Forbidden || PermissionError().IsMatch(ex.Message))
        {
            throw new InvalidOperationException($"Missing permission: {ex.Message} – the app registration probably lacks a Graph permission or admin consent");
        }
    }

    [GeneratedRegex("<c0>(.*?)</c0>")]
    private static partial Regex Highlight();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex("insufficient privileges|access is denied|consent|AADSTS65001", RegexOptions.IgnoreCase)]
    private static partial Regex PermissionError();
}
