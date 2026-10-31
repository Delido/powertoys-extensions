using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.G4f;

public sealed class G4fSettings : SettingsBase
{
    public static readonly string[] Languages =
    [
        "English", "German", "French", "Spanish", "Italian", "Portuguese", "Dutch", "Polish", "Czech", "Danish", "Swedish", "Norwegian",
        "Finnish", "Greek", "Turkish", "Russian", "Ukrainian", "Hungarian", "Romanian", "Croatian", "Bulgarian", "Chinese", "Japanese",
        "Korean", "Vietnamese", "Hindi", "Arabic", "Hebrew", "Indonesian", "Thai",
    ];

    private readonly ChoiceSetSetting _language = new("defaultLanguage", "Default language", "Language of AI responses for commands like Summarize or Explain (not the extension UI)", [.. Languages.Select(l => new ChoiceSetSetting.Choice(l, l))]);
    private readonly ToggleSetting _translateDefault = new("translateUseDefaultLanguage", "Translate to the default language", "Translate skips the language choice and always uses the default language", false);
    private readonly ToggleSetting _smartNames = new("smartChatNaming", "Smart chat naming", "Let the AI name a new chat after the first message", false);

    public G4fSettings()
        : base("g4f")
    {
        Settings.Add(_language);
        Settings.Add(_translateDefault);
        Settings.Add(_smartNames);
        Load();
    }

    public string DefaultLanguage => _language.Value is { Length: > 0 } l ? l : "English";

    public bool TranslateUseDefaultLanguage => _translateDefault.Value;

    public bool SmartChatNaming => _smartNames.Value;
}

public sealed class CustomApi
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>OpenAI (chat/completions) or Anthropic (messages).</summary>
    public string Type { get; set; } = "OpenAI";

    /// <summary>API key, encrypted for the current Windows user (DPAPI).</summary>
    public string KeyProtected { get; set; } = string.Empty;

    public List<string> Models { get; set; } = [];

    public string Label => Name.Length > 0 ? Name : Url;

    public string Key
    {
        get
        {
            if (KeyProtected.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(KeyProtected), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return string.Empty;
            }
        }

        set => KeyProtected = value.Length == 0 ? string.Empty : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }
}

public sealed class AiPreset
{
    public string Name { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public double Creativity { get; set; } = 0.7;

    public string SystemPrompt { get; set; } = string.Empty;

    public bool IsDefault { get; set; }
}

public sealed class CustomCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;
}

public sealed class ChatMessage
{
    public long Id { get; set; } = DateTime.UtcNow.Ticks;

    public string Prompt { get; set; } = string.Empty;

    public string Answer { get; set; } = string.Empty;

    public DateTime Created { get; set; } = DateTime.Now;

    public bool Finished { get; set; }

    public List<string> Files { get; set; } = [];
}

public sealed class Chat
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];

    public string Name { get; set; } = "New Chat";

    public DateTime Created { get; set; } = DateTime.Now;

    public bool Pinned { get; set; }

    public string Model { get; set; } = string.Empty;

    public double Creativity { get; set; } = 0.7;

    public string SystemPrompt { get; set; } = string.Empty;

    public List<ChatMessage> Messages { get; set; } = [];
}

public sealed class ImageEntry
{
    public string Prompt { get; set; } = string.Empty;

    public string File { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public DateTime Created { get; set; } = DateTime.Now;
}

/// <summary>All data of GPT4Free in LocalState\g4f as JSON (API keys DPAPI-encrypted).</summary>
public sealed class G4fState
{
    public List<CustomApi> Apis { get; set; } = [];

    public string DefaultModel { get; set; } = string.Empty;

    public Dictionary<string, string> CommandModels { get; set; } = [];

    public string TranslateLanguage { get; set; } = "English";

    public string ImageModel { get; set; } = string.Empty;

    public string ImageSize { get; set; } = "1024x1024";

    public List<AiPreset> Presets { get; set; } = [];

    public List<CustomCommand> Commands { get; set; } = [];

    public List<Chat> Chats { get; set; } = [];

    public string CurrentChat { get; set; } = string.Empty;

    public List<ImageEntry> Images { get; set; } = [];
}

public sealed record ModelRef(CustomApi Api, string Model)
{
    public string Key => $"{Api.Id}|{Model}";

    public string Label => $"{Api.Label} ({Model})";
}

public sealed record AiMessage(string Role, string Content, IReadOnlyList<string>? Files = null);

public sealed class G4fStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _file = Path.Combine(Paths.For("g4f"), "data.json");
    private readonly Lock _gate = new();

    public G4fStore()
    {
        try
        {
            State = File.Exists(_file) ? JsonSerializer.Deserialize<G4fState>(File.ReadAllText(_file), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Error("g4f: data.json unreadable, starting empty", ex);
            File.Copy(_file, _file + ".broken", overwrite: true);
            State = new();
        }
    }

    public G4fState State { get; }

    public string ImagesDir => Directory.CreateDirectory(Path.Combine(Paths.For("g4f"), "images")).FullName;

    public IEnumerable<ModelRef> Models => State.Apis.SelectMany(a => a.Models.Select(m => new ModelRef(a, m)));

    public ModelRef? Find(string? key) =>
        string.IsNullOrEmpty(key) ? null : Models.FirstOrDefault(m => m.Key == key);

    /// <summary>The model of a command: its own choice, else the default, else the first model.</summary>
    public ModelRef? ModelFor(string? commandId = null) =>
        (commandId is not null ? Find(State.CommandModels.GetValueOrDefault(commandId)) : null)
        ?? Find(State.DefaultModel)
        ?? Models.FirstOrDefault();

    public void Save()
    {
        lock (_gate)
        {
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(State, Json));
            File.Move(temp, _file, overwrite: true);
        }
    }
}

/// <summary>Chat completions (streamed), model lists and image generation for OpenAI- and Anthropic-style APIs.</summary>
public static partial class AiClient
{
    private static readonly HttpClient Client = CreateClient();

    public static readonly (string Name, string Url, string Type)[] Templates =
    [
        ("OpenAI", "https://api.openai.com/v1", "OpenAI"),
        ("Anthropic", "https://api.anthropic.com/v1", "Anthropic"),
        ("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "OpenAI"),
        ("OpenRouter", "https://openrouter.ai/api/v1", "OpenAI"),
        ("Groq", "https://api.groq.com/openai/v1", "OpenAI"),
        ("Mistral", "https://api.mistral.ai/v1", "OpenAI"),
        ("DeepSeek", "https://api.deepseek.com/v1", "OpenAI"),
        ("DeepInfra", "https://api.deepinfra.com/v1/openai", "OpenAI"),
        ("Ollama (local)", "http://localhost:11434/v1", "OpenAI"),
        ("LM Studio (local)", "http://localhost:1234/v1", "OpenAI"),
    ];

    public static async Task<List<string>> ListModels(CustomApi api, CancellationToken token = default)
    {
        using var request = Request(api, HttpMethod.Get, "models");
        using var response = await Send(request, token);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
        return [.. (json?["data"] as JsonArray ?? json?["models"] as JsonArray ?? []).Select(m => Graph.Str(m?["id"]) is { Length: > 0 } id ? id : Graph.Str(m?["name"])).Where(id => id.Length > 0).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Streams the answer; yields the text pieces as they arrive.</summary>
    public static async IAsyncEnumerable<string> Stream(ModelRef model, IReadOnlyList<AiMessage> messages, double creativity, [EnumeratorCancellation] CancellationToken token = default)
    {
        var anthropic = model.Api.Type == "Anthropic";
        var body = anthropic ? AnthropicBody(model.Model, messages, creativity) : OpenAiBody(model.Model, messages, creativity);
        using var request = Request(model.Api, HttpMethod.Post, anthropic ? "messages" : "chat/completions");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await Send(request, token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        var thinking = false;
        while (await reader.ReadLineAsync(token) is { } raw)
        {
            var line = raw.Trim();
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                line = line[5..].Trim();
            }

            if (line.Length == 0 || line == "[DONE]" || !line.StartsWith('{'))
            {
                continue;
            }

            JsonNode? json;
            try
            {
                json = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (json?["error"] is { } error)
            {
                throw new InvalidOperationException(Graph.Str(error["message"]) is { Length: > 0 } m ? m : error.ToJsonString());
            }

            var piece = new StringBuilder();
            if (anthropic)
            {
                var delta = json?["delta"];
                if (Graph.Str(delta?["thinking"]) is { Length: > 0 } t)
                {
                    piece.Append(thinking ? string.Empty : "\n<thinking>\n").Append(t);
                    thinking = true;
                }

                if (Graph.Str(delta?["text"]) is { Length: > 0 } text)
                {
                    piece.Append(thinking ? "\n</thinking>\n\n" : string.Empty).Append(text);
                    thinking = false;
                }
            }
            else
            {
                var choice = json?["choices"]?[0];
                var delta = choice?["delta"] ?? choice?["message"];
                if (Graph.Str(delta?["reasoning_content"]) is { Length: > 0 } t)
                {
                    piece.Append(thinking ? string.Empty : "\n<thinking>\n").Append(t);
                    thinking = true;
                }

                if (Graph.Str(delta?["content"]) is { Length: > 0 } text)
                {
                    piece.Append(thinking ? "\n</thinking>\n\n" : string.Empty).Append(text);
                    thinking = false;
                }
            }

            if (piece.Length > 0)
            {
                yield return piece.ToString();
            }
        }
    }

    /// <summary>The whole answer at once (chat names and the like).</summary>
    public static async Task<string> Complete(ModelRef model, IReadOnlyList<AiMessage> messages, double creativity = 0.7, CancellationToken token = default)
    {
        var text = new StringBuilder();
        await foreach (var piece in Stream(model, messages, creativity, token))
        {
            text.Append(piece);
        }

        return ThinkingBlock().Replace(text.ToString(), string.Empty).Trim();
    }

    /// <summary>OpenAI-style image generation; returns the PNG bytes.</summary>
    public static async Task<byte[]> GenerateImage(ModelRef model, string prompt, string size, CancellationToken token = default)
    {
        using var request = Request(model.Api, HttpMethod.Post, "images/generations");
        var body = new JsonObject { ["model"] = model.Model, ["prompt"] = prompt, ["n"] = 1, ["size"] = size };
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await Send(request, token);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
        var image = json?["data"]?[0];
        if (Graph.Str(image?["b64_json"]) is { Length: > 0 } b64)
        {
            return Convert.FromBase64String(b64);
        }

        if (Graph.Str(image?["url"]) is { Length: > 0 } url)
        {
            return await Client.GetByteArrayAsync(url, token);
        }

        throw new InvalidOperationException("The API returned no image");
    }

    private static JsonObject OpenAiBody(string model, IReadOnlyList<AiMessage> messages, double creativity) => new()
    {
        ["model"] = model,
        ["stream"] = true,
        ["temperature"] = creativity,
        ["messages"] = new JsonArray([.. messages.Select(m => (JsonNode)new JsonObject
        {
            ["role"] = m.Role,
            ["content"] = Images(m) is { Count: > 0 } images
                ? new JsonArray([(JsonNode)new JsonObject { ["type"] = "text", ["text"] = m.Content }, .. images.Select(i => (JsonNode)new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{i.Mime};base64,{i.Data}" } })])
                : m.Content,
        })]),
    };

    private static JsonObject AnthropicBody(string model, IReadOnlyList<AiMessage> messages, double creativity)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["max_tokens"] = 8192,
            ["temperature"] = Math.Min(1, creativity),
            ["messages"] = new JsonArray([.. messages.Where(m => m.Role != "system").Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = Images(m) is { Count: > 0 } images
                    ? new JsonArray([.. images.Select(i => (JsonNode)new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = i.Mime, ["data"] = i.Data } }), new JsonObject { ["type"] = "text", ["text"] = m.Content }])
                    : m.Content,
            })]),
        };
        if (string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content)) is { Length: > 0 } system)
        {
            body["system"] = system;
        }

        return body;
    }

    private static List<(string Mime, string Data)> Images(AiMessage message) =>
        [.. (message.Files ?? []).Where(Attachments.IsImage).Where(File.Exists).Select(f => (Attachments.Mime(f), Convert.ToBase64String(File.ReadAllBytes(f))))];

    private static HttpRequestMessage Request(CustomApi api, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{Http.TrimUrl(api.Url)}/{path}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (api.Key is { Length: > 0 } key)
        {
            if (api.Type == "Anthropic")
            {
                request.Headers.Add("x-api-key", key);
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
        }

        if (api.Type == "Anthropic")
        {
            request.Headers.Add("anthropic-version", "2023-06-01");
        }

        return request;
    }

    private static async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
    {
        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"{request.RequestUri?.Host} not reachable: {ex.InnerException?.Message ?? ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(token);
            response.Dispose();
            string? message = null;
            try
            {
                var json = JsonNode.Parse(text);
                message = Graph.Str(json?["error"]?["message"]) is { Length: > 0 } m ? m : Graph.Str(json?["error"]) is { Length: > 0 } e ? e : null;
            }
            catch (JsonException)
            {
                // not JSON
            }

            throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {message ?? text[..Math.Min(300, text.Length)]}");
        }

        return response;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Delido-PowerToys-Extensions/1.0");
        return client;
    }

    [GeneratedRegex(@"<thinking>[\s\S]*?</thinking>")]
    private static partial Regex ThinkingBlock();
}

/// <summary>Files attached to a prompt: images go to vision models, text files are included as text.</summary>
public static class Attachments
{
    private const int MaxTextChars = 200_000;
    private static readonly HashSet<string> ImageTypes = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    public const string Filter = "Supported files\0*.png;*.jpg;*.jpeg;*.gif;*.webp;*.txt;*.md;*.csv;*.json;*.xml;*.yaml;*.yml;*.log;*.ini;*.cfg;*.conf;*.ps1;*.py;*.js;*.ts;*.cs;*.go;*.java;*.c;*.cpp;*.h;*.sql;*.html;*.css;*.sh\0All files\0*.*\0";

    public static bool IsImage(string path) => ImageTypes.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/png",
    };

    /// <summary>The prompt with the text files appended.</summary>
    public static string WithTextFiles(string prompt, IEnumerable<string> files)
    {
        var text = new StringBuilder(prompt);
        foreach (var file in files.Where(f => !IsImage(f) && File.Exists(f)))
        {
            var content = File.ReadAllText(file);
            if (content.Length > MaxTextChars)
            {
                content = content[..MaxTextChars] + "\n[… truncated]";
            }

            text.Append(CultureInfo.InvariantCulture, $"\n\n--- File: {Path.GetFileName(file)} ---\n{content}");
        }

        return text.ToString();
    }
}
