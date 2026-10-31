using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.SnipeIt;

public sealed class SnipeItSettings : SettingsBase
{
    private readonly TextSetting _url = new("baseUrl", "Snipe-IT URL", "Base URL of your Snipe-IT instance", string.Empty) { Placeholder = "https://assets.example.com" };
    private readonly SecretSetting _token = new("apiToken", "API Token", "Personal API key (Snipe-IT: your profile → Manage API Keys), stored encrypted");
    private readonly ToggleSetting _insecure = new("allowInsecure", "Skip TLS certificate validation", "Only for internal servers with self-signed certificates", false);

    public SnipeItSettings()
        : base("snipeit")
    {
        Settings.Add(_url);
        Settings.Add(_token);
        Settings.Add(_insecure);
        Load();
    }

    public string BaseUrl => Http.TrimUrl(_url.Value ?? string.Empty);

    public bool Configured => BaseUrl.Length > 0 && _token.HasValue;

    public AuthenticationHeaderValue Auth => new("Bearer", _token.Value);

    public bool AllowInsecure => _insecure.Value;
}

public sealed record Ref(int Id, string? Name);

public sealed record DateField(string? Date, string? Datetime, string? Formatted)
{
    public override string ToString() => Formatted ?? Date ?? string.Empty;

    public DateTime? Value => DateTime.TryParse(Datetime ?? Date, out var d) ? d : null;
}

public sealed record StatusLabel(int Id, string? Name, string? StatusType, string? StatusMeta);

public sealed record AssignedTo(int Id, string? Name, string? Username, string? FirstName, string? LastName, string? Type)
{
    public string Display => SnipeItApi.Decode(Name) is { Length: > 0 } n ? n
        : string.Join(" ", new[] { FirstName, LastName }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } full ? full
        : Username ?? string.Empty;
}

public sealed record CustomField(string? Field, string? Value);

public sealed record AvailableActions(bool? Checkout, bool? Checkin);

public sealed record Asset(
    int Id,
    string? Name,
    string AssetTag,
    string? Serial,
    Ref? Model,
    string? ModelNumber,
    StatusLabel? StatusLabel,
    Ref? Category,
    Ref? Manufacturer,
    Ref? Supplier,
    Ref? Location,
    Ref? RtdLocation,
    Ref? Company,
    AssignedTo? AssignedTo,
    string? Notes,
    string? OrderNumber,
    DateField? PurchaseDate,
    JsonElement? PurchaseCost,
    DateField? WarrantyExpires,
    DateField? LastAuditDate,
    DateField? NextAuditDate,
    DateField? ExpectedCheckin,
    string? Image,
    JsonElement? CustomFields,
    AvailableActions? AvailableActions)
{
    public string Title => SnipeItApi.Decode(Name) is { Length: > 0 } n ? n : SnipeItApi.Decode(Model?.Name) is { Length: > 0 } m ? m : AssetTag;

    public bool CanCheckout => (AvailableActions?.Checkout ?? AssignedTo is null) && AssignedTo is null;

    public bool CanCheckin => (AvailableActions?.Checkin ?? AssignedTo is not null) && AssignedTo is not null;

    public string StatusText
    {
        get
        {
            var label = SnipeItApi.Decode(StatusLabel?.Name);
            return StatusLabel?.StatusMeta == "deployed" && AssignedTo is not null ? $"{label} → {AssignedTo.Display}" : label;
        }
    }

    public IEnumerable<(string Label, string Value)> Fields =>
        CustomFields is { ValueKind: JsonValueKind.Object } fields
            ? fields.EnumerateObject()
                .Select(p => (SnipeItApi.Decode(p.Name), SnipeItApi.Decode(p.Value.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)))
                .Where(f => f.Item2.Length > 0)
            : [];
}

public sealed record User(
    int Id,
    string Name,
    string? Username,
    string? Email,
    string? EmployeeNum,
    string? Jobtitle,
    Ref? Department,
    int? AssetsCount,
    int? LicensesCount,
    int? AccessoriesCount);

public sealed record License(
    int Id,
    string Name,
    int Seats,
    int FreeSeatsCount,
    DateField? ExpirationDate,
    Ref? Manufacturer,
    Ref? Category,
    string? LicenseName,
    string? LicenseEmail,
    string? ProductKey,
    string? OrderNumber,
    string? Notes);

public sealed record StockItem(int Id, string Name, int Qty, int? RemainingQty, int? Remaining, int? MinQty, int? MinAmt, Ref? Category, Ref? Manufacturer, Ref? Location, string? ModelNumber, string? ItemNo)
{
    public int Left => RemainingQty ?? Remaining ?? Qty;

    public int Minimum => MinQty ?? MinAmt ?? 0;

    public bool Low => Minimum > 0 ? Left <= Minimum : Left == 0;
}

public sealed record RowSet<T>(int Total, T[] Rows);

public sealed class SnipeItApi(SnipeItSettings settings)
{
    private static readonly JsonSerializerOptions Json = new(Http.SnakeJson) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    public SnipeItSettings Settings => settings;

    public string WebUrl(string kind, int id) => $"{settings.BaseUrl}/{kind}/{id}";

    public static string Decode(string? value) => WebUtility.HtmlDecode(value ?? string.Empty);

    public Task<RowSet<T>> List<T>(string path, params (string Key, string? Value)[] query) =>
        Get<RowSet<T>>($"{path}?{Http.Query([.. query.Where(q => !string.IsNullOrEmpty(q.Value))])}");

    public async Task<T> Get<T>(string path, CancellationToken token = default)
    {
        var node = await Send(HttpMethod.Get, path, null, token);
        return node.Deserialize<T>(Json) ?? throw new ApiException("Empty response");
    }

    /// <summary>Finds an asset by exact tag, then by serial, then by free-text search.</summary>
    public async Task<Asset[]> Lookup(string query, CancellationToken token)
    {
        try
        {
            var node = await Send(HttpMethod.Get, $"hardware/bytag/{Uri.EscapeDataString(query)}", null, token);
            if (node["rows"] is JsonArray rows && rows.Count > 0)
            {
                return rows.Deserialize<Asset[]>(Json) ?? [];
            }

            if (node["id"] is not null)
            {
                return [node.Deserialize<Asset>(Json)!];
            }
        }
        catch (ApiException)
        {
            // not a tag
        }

        try
        {
            var bySerial = await Get<RowSet<Asset>>($"hardware/byserial/{Uri.EscapeDataString(query)}", token);
            if (bySerial.Rows.Length > 0)
            {
                return bySerial.Rows;
            }
        }
        catch (ApiException)
        {
            // not a serial
        }

        return (await List<Asset>("hardware", ("search", query), ("limit", "50"))).Rows;
    }

    public Task Checkout(int assetId, string targetType, int targetId, string note, DateTime? expected)
    {
        var body = new JsonObject { ["checkout_to_type"] = targetType, ["note"] = note };
        body[targetType == "user" ? "assigned_user" : "assigned_location"] = targetId;
        if (expected is { } e)
        {
            body["expected_checkin"] = e.ToString("yyyy-MM-dd");
        }

        return Send(HttpMethod.Post, $"hardware/{assetId}/checkout", body);
    }

    public Task Checkin(int assetId, string note) => Send(HttpMethod.Post, $"hardware/{assetId}/checkin", new JsonObject { ["note"] = note });

    /// <summary>Snipe-IT often reports errors with HTTP 200 and {"status":"error","messages":…}.</summary>
    private async Task<JsonNode> Send(HttpMethod method, string path, JsonObject? body, CancellationToken token = default)
    {
        if (!settings.Configured)
        {
            throw new InvalidOperationException("Snipe-IT is not set up yet – open the settings of this extension");
        }

        var request = Http.Request(method, $"{settings.BaseUrl}/api/v1/{path}", settings.Auth);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await Http.Send(Http.Client(settings.AllowInsecure), request, token);
        var text = await response.Content.ReadAsStringAsync(token);
        JsonNode node;
        try
        {
            node = JsonNode.Parse(text) ?? new JsonObject();
        }
        catch (JsonException)
        {
            throw new ApiException("Unexpected response from Snipe-IT – is the URL correct?");
        }

        if (node is JsonObject obj && obj["status"]?.GetValue<string>() == "error")
        {
            throw new ApiException(Messages(obj["messages"] ?? obj["message"] ?? obj["error"]) is { Length: > 0 } m ? m : "Snipe-IT reported an error");
        }

        return node;
    }

    private static string Messages(JsonNode? messages) => messages switch
    {
        null => string.Empty,
        JsonValue v => v.ToString(),
        JsonArray a => string.Join(", ", a.Select(Messages)),
        JsonObject o => string.Join("; ", o.Select(p => $"{p.Key}: {Messages(p.Value)}")),
        _ => messages.ToJsonString(),
    };
}
