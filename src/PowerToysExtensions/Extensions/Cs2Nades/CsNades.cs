using System.Text.Json;
using System.Text.RegularExpressions;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.Cs2Nades;

/// <summary>
/// Reads the lineups that csnades.gg shows on its public map pages. The site has no API: the nades are
/// part of the Next.js RSC payload (self.__next_f.push chunks) embedded in each page.
/// </summary>
public static partial class CsNades
{
    public const string Site = "https://csnades.gg";

    // Premier Season Five map pool (since July 2026). Update when Valve rotates the pool.
    public static readonly (string Slug, string Name)[] PremierMaps =
        [("ancient", "Ancient"), ("anubis", "Anubis"), ("cache", "Cache"), ("dust2", "Dust 2"), ("inferno", "Inferno"), ("mirage", "Mirage"), ("nuke", "Nuke")];

    public static readonly (string Slug, string Name)[] OtherMaps = [("overpass", "Overpass"), ("train", "Train"), ("vertigo", "Vertigo")];

    public static readonly (string Type, string Path, string Title)[] Types =
        [("smoke", "smokes", "Smokes"), ("molotov", "molotovs", "Molotovs"), ("flashbang", "flashbangs", "Flashbangs"), ("he", "hegrenades", "HE Grenades")];

    private const string NadeStart = "{\"id\":\"nade_";
    private static readonly string CacheDir = Paths.For("cs2-nades");

    public static string MapName(string slug) => PremierMaps.Concat(OtherMaps).FirstOrDefault(m => m.Slug == slug).Name ?? slug;

    public static string TypeTitle(string type) => Types.FirstOrDefault(t => t.Type == type).Title ?? type;

    /// <summary>Lineups of a map cached on disk from the last load, or null.</summary>
    public static Nade[]? Cached(string map)
    {
        try
        {
            var file = Path.Combine(CacheDir, $"{map}.json");
            return File.Exists(file) ? JsonSerializer.Deserialize<Nade[]>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    public static async Task<Nade[]> Load(string map, CancellationToken token)
    {
        var pages = await Task.WhenAll(Types.Select(async t =>
        {
            // Smokes are the map's start page, the other types have their own sub page
            var html = await Fetch(t.Type == "smoke" ? $"{Site}/{map}" : $"{Site}/{map}/{t.Path}", token);
            return Parse(html, map, t.Type, t.Path);
        }));
        var nades = pages.SelectMany(p => p).ToArray();
        if (nades.Length == 0)
        {
            throw new InvalidOperationException("No lineups found – csnades.gg may have changed its page format");
        }

        File.WriteAllText(Path.Combine(CacheDir, $"{map}.json"), JsonSerializer.Serialize(nades));
        return nades;
    }

    public static Nade[] Parse(string html, string map, string type, string path)
    {
        var payload = string.Concat(Chunk().Matches(html).Select(m => JsonSerializer.Deserialize<string>($"\"{m.Groups[1].Value}\"")));
        var nades = new List<Nade>();
        var seen = new HashSet<string>();
        for (var i = payload.IndexOf(NadeStart, StringComparison.Ordinal); i != -1; i = payload.IndexOf(NadeStart, i + 1, StringComparison.Ordinal))
        {
            var end = ObjectEnd(payload, i);
            if (end == -1)
            {
                break;
            }

            RawNade? raw;
            try
            {
                raw = JsonSerializer.Deserialize<RawNade>(payload.AsSpan(i, end - i + 1), Http.Json);
            }
            catch (JsonException)
            {
                continue;
            }

            var hq = raw?.Assets?.VideoHq?.Mp4;
            if (raw?.Id is null || raw.Slug is null || hq is null || raw.Type != type || !seen.Add(raw.Id))
            {
                continue;
            }

            nades.Add(new Nade(
                raw.Id,
                map,
                type,
                raw.Team == "ct" ? "ct" : "t",
                raw.TitleFrom ?? "?",
                raw.TitleTo ?? raw.Slug,
                raw.Technique ?? string.Empty,
                raw.Movement ?? string.Empty,
                raw.Precision ?? string.Empty,
                raw.Beginner == true,
                hq,
                raw.Assets?.VideoLq?.Mp4 ?? hq,
                raw.Assets?.Thumbnail ?? string.Empty,
                raw.Assets?.Lineup ?? string.Empty,
                $"{Site}/{map}/{path}/{raw.Slug}"));
        }

        return [.. nades];
    }

    public static string Technique(string v) => v switch
    {
        "left" => "Left click",
        "right" => "Right click",
        "both" => "Left + right click",
        "left_jump" => "Jump throw (left click)",
        "right_jump" => "Jump throw (right click)",
        "both_jump" => "Jump throw (left + right click)",
        _ => v.Replace('_', ' '),
    };

    public static string Movement(string v) => v switch
    {
        "stationary" => "Standing",
        "running" => "Running",
        "walking" => "Walking (Shift)",
        "crouched_stationary" => "Crouching",
        "crouched_walking" => "Crouch walking",
        "crouched_running" => "Crouch running",
        _ => v.Replace('_', ' '),
    };

    public static string Precision(string v) => v switch
    {
        "loose" => "Loose",
        "precise" => "Precise",
        "very_precise" => "Very precise",
        _ => v.Replace('_', ' '),
    };

    private static async Task<string> Fetch(string url, CancellationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) PowerToys-CS2-Nades");
        try
        {
            using var response = await Http.Send(Http.Client(), request, token);
            return await response.Content.ReadAsStringAsync(token);
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            return string.Empty;
        }
    }

    // Index of the brace closing the object that starts at `start`, skipping braces inside strings
    private static int ObjectEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    [GeneratedRegex("""self\.__next_f\.push\(\[1,"((?:[^"\\]|\\.)*)"\]\)""")]
    private static partial Regex Chunk();

    private sealed record Video(string? Mp4);

    private sealed record RawAssets(Video? VideoHq, Video? VideoLq, string? Thumbnail, string? Lineup);

    private sealed record RawNade(string? Id, string? Slug, string? Type, string? Team, string? TitleFrom, string? TitleTo, string? Technique, string? Movement, string? Precision, bool? Beginner, RawAssets? Assets);
}

public sealed record Nade(
    string Id,
    string Map,
    string Type,
    string Team,
    string From,
    string To,
    string Technique,
    string Movement,
    string Precision,
    bool Recommended,
    string VideoHq,
    string VideoLq,
    string Thumbnail,
    string Lineup,
    string Url);
