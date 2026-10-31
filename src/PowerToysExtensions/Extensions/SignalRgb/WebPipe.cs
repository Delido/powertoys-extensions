using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PowerToysExtensions.Extensions.SignalRgb;

public sealed record SensorHistory(double[] Seconds, double[] Minutes, double[] FiveMinutes, double[] Hours);

public sealed record Sensor(string Id, string Uid, string Name, string Group, double Value, string ValueStr, string Unit, double Min, double Max, string Type, SensorHistory History)
{
    /// <summary>"68°C" -> "68 °C".</summary>
    public string Display => Regex.Replace(ValueStr, @"^(-?[\d.,]+)\s*(?=\S)", "$1 ");

    public string Category => Type switch
    {
        "TemperatureProvider" => "temperature",
        "SpeedProvider" => "cooling",
        "PowerProvider" or "VoltageProvider" or "ClockProvider" => "power",
        "LatencyProvider" or "DataRateProvider" => "io",

        // fan duty cycles are reported as load
        _ => Regex.IsMatch(Name, "fan|pump", RegexOptions.IgnoreCase) ? "cooling" : "load",
    };
}

/// <summary>
/// SignalRGB exposes its internal Qt objects over a QWebChannel on a local WebSocket ("WebPipe"). It is
/// undocumented, so everything is read defensively. Connecting costs SignalRGB a 9.5 MB snapshot, and a
/// connected reader keeps it busy, so the page connects only while it is shown.
/// </summary>
public sealed class WebPipe : IAsyncDisposable
{
    private const int PropertyUpdate = 2;
    private const int Init = 3;
    private const int Idle = 4;
    private const int Response = 10;

    private readonly ClientWebSocket _socket = new();
    private readonly Dictionary<string, (Dictionary<string, JsonNode?> Props, Dictionary<string, string> Names)> _tracked = [];
    private readonly List<string> _order = [];
    private readonly CancellationTokenSource _cts = new();

    public event Action<Sensor[]>? Updated;

    public event Action<Exception>? Failed;

    public async Task Connect(string url)
    {
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        try
        {
            await _socket.ConnectAsync(new Uri(url), _cts.Token);
        }
        catch (WebSocketException)
        {
            throw new InvalidOperationException("SignalRGB is not running (WebPipe not reachable)");
        }

        await Send(new JsonObject { ["type"] = Init, ["id"] = 0 });
        _ = Receive();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // gone anyway
        }

        _socket.Dispose();
    }

    private static Sensor ToSensor(string id, Dictionary<string, JsonNode?> p)
    {
        string S(string key) => p.TryGetValue(key, out var v) && v is not null ? v.ToString() : string.Empty;
        double D(string key) => double.TryParse(S(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        double[] H(string key) => p.TryGetValue("history", out var h) && h?[key] is JsonArray a ? [.. a.Select(x => x?.GetValue<double>() ?? 0)] : [];
        return new Sensor(id, S("uid"), S("name"), S("group"), D("value"), S("valueStr"), S("unit"), D("min"), D("max"), S("type"), new SensorHistory(H("seconds"), H("minutes"), H("fiveMinutes"), H("hours")));
    }

    private async Task Send(JsonObject message)
    {
        if (_socket.State == WebSocketState.Open)
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, _cts.Token);
        }
    }

    private async Task Receive()
    {
        var buffer = new byte[1 << 20];
        var lastEmit = DateTime.MinValue;
        var dirty = false;
        try
        {
            while (!_cts.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new InvalidOperationException("Connection to SignalRGB was closed");
                    }

                    stream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                JsonNode? message;
                try
                {
                    message = JsonNode.Parse(stream.ToArray());
                }
                catch (JsonException)
                {
                    continue;
                }

                var type = message?["type"]?.GetValue<int>();
                if (type == Response && message?["id"]?.GetValue<int>() == 0)
                {
                    var sensors = (message["data"]?["monitor"]?["properties"] as JsonArray)?.FirstOrDefault(p => p?[1]?.ToString() == "sensors")?[3] as JsonArray;
                    if (sensors is null)
                    {
                        throw new InvalidOperationException("SignalRGB did not report any sensors. Is monitoring enabled?");
                    }

                    foreach (var r in sensors)
                    {
                        var id = r?["id"]?.ToString() ?? string.Empty;
                        var props = r?["data"]?["properties"] as JsonArray ?? [];
                        _tracked[id] = (
                            props.Where(x => x is not null).ToDictionary(x => x![1]!.ToString(), x => x![3]?.DeepClone()),
                            props.Where(x => x is not null).ToDictionary(x => x![0]!.ToString(), x => x![1]!.ToString()));
                        _order.Add(id);
                    }

                    Emit();
                    lastEmit = DateTime.UtcNow;
                    await Send(new JsonObject { ["type"] = Idle });
                    continue;
                }

                if (type == PropertyUpdate && message?["data"] is JsonArray updates)
                {
                    foreach (var u in updates)
                    {
                        if (u?["object"]?.ToString() is not { } obj || !_tracked.TryGetValue(obj, out var entry) || u["properties"] is not JsonObject changed)
                        {
                            continue;
                        }

                        foreach (var (index, value) in changed)
                        {
                            if (entry.Names.TryGetValue(index, out var name))
                            {
                                entry.Props[name] = value?.DeepClone();
                            }
                        }

                        dirty = true;
                    }

                    // at most one update per second to Command Palette
                    if (dirty && DateTime.UtcNow - lastEmit > TimeSpan.FromSeconds(1))
                    {
                        Emit();
                        dirty = false;
                        lastEmit = DateTime.UtcNow;
                    }

                    await Send(new JsonObject { ["type"] = Idle });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // closed by us
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
        {
            Failed?.Invoke(ex);
        }
    }

    private void Emit() => Updated?.Invoke([.. _order.Select(id => ToSensor(id, _tracked[id].Props))]);
}
