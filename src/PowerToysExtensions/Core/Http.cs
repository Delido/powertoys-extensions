using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PowerToysExtensions.Core;

/// <summary>Shared HTTP helpers. Errors become <see cref="ApiException"/> with a readable message.</summary>
public static class Http
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>For APIs with snake_case property names (BookStack, Snipe-IT, Paperless …).</summary>
    public static JsonSerializerOptions SnakeJson { get; } = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static readonly HttpClient Default = Create(ignoreCertificateErrors: false);
    private static readonly HttpClient Lenient = Create(ignoreCertificateErrors: true);

    /// <summary>A shared client; with ignoreCertificateErrors for self-signed homelab servers.</summary>
    public static HttpClient Client(bool ignoreCertificateErrors = false) => ignoreCertificateErrors ? Lenient : Default;

    public static async Task<T> GetJson<T>(HttpClient client, HttpRequestMessage request, CancellationToken token = default, JsonSerializerOptions? options = null)
    {
        using var response = await Send(client, request, token);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonSerializer.DeserializeAsync<T>(stream, options ?? Json, token) ?? throw new ApiException("Empty response");
    }

    public static async Task<HttpResponseMessage> Send(HttpClient client, HttpRequestMessage request, CancellationToken token = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException($"{request.RequestUri?.Host} not reachable: {ex.InnerException?.Message ?? ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new ApiException($"{request.RequestUri?.Host} did not answer in time", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var body = await response.Content.ReadAsStringAsync(token);
        response.Dispose();
        throw new ApiException(ErrorMessage(response.StatusCode, body), response.StatusCode);
    }

    /// <summary>Builds a request with JSON accept header and optional auth.</summary>
    public static HttpRequestMessage Request(HttpMethod method, string url, AuthenticationHeaderValue? auth = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = auth;
        return request;
    }

    public static string Query(params (string Key, string? Value)[] pairs) =>
        string.Join("&", pairs.Where(p => p.Value is not null).Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}"));

    public static string TrimUrl(string url) => url.Trim().TrimEnd('/');

    private static HttpClient Create(bool ignoreCertificateErrors)
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        if (ignoreCertificateErrors)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Delido-PowerToys-Extensions/1.0");
        return client;
    }

    private static string ErrorMessage(HttpStatusCode status, string body)
    {
        var text = $"{(int)status} {status}";
        try
        {
            var json = JsonNode.Parse(body);
            var message = json?["error"]?["message"]?.GetValue<string>()
                ?? json?["message"]?.GetValue<string>()
                ?? json?["detail"]?.GetValue<string>()
                ?? json?["title"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message))
            {
                return $"{text}: {message}";
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // not JSON
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => $"{text}: check the token in the settings",
            HttpStatusCode.Forbidden => $"{text}: the token lacks permissions",
            _ => text,
        };
    }
}

public sealed class ApiException : Exception
{
    public ApiException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    public ApiException(string message, HttpStatusCode status)
        : base(message)
    {
        Status = status;
    }

    public ApiException()
    {
    }

    public ApiException(string message)
        : base(message)
    {
    }

    public HttpStatusCode? Status { get; }
}
