using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace PowerToysExtensions.Extensions.Mtr;

public sealed record Hop(int Ttl, string? Addr, double? Rtt, string Status);

public sealed record Round(int Number, DateTimeOffset Time, Hop[] Hops);

/// <summary>
/// ICMP echo requests with increasing TTL, like WinMTR, via the Windows ICMP API (no administrator rights).
/// </summary>
public sealed class MtrProbe(string target, int timeoutMs, int maxHops, int size)
{
    private const int StaggerMs = 15; // routers rate-limit bursts
    private readonly HashSet<string> _resolved = [];
    private int _destHop;
    private int _lastAnswer;

    public event Action<string, string>? HostResolved;

    public IPAddress? Ip { get; private set; }

    public async Task<IPAddress> Resolve(CancellationToken token)
    {
        if (IPAddress.TryParse(target, out var ip))
        {
            return Ip = ip;
        }

        var addresses = await Dns.GetHostAddressesAsync(target, AddressFamily.InterNetwork, token);
        return Ip = addresses.FirstOrDefault() ?? throw new InvalidOperationException($"{target} could not be resolved");
    }

    public async Task<Round> Run(int number, CancellationToken token)
    {
        var ip = Ip ?? await Resolve(token);
        var start = DateTimeOffset.Now;
        var limit = _destHop > 0 ? Math.Min(_destHop + 2, maxHops)
            : number > 1 && _lastAnswer > 0 ? Math.Min(_lastAnswer + 5, maxHops) // target not reached: don't keep probing 30 silent hops
            : maxHops;

        var payload = Encoding.ASCII.GetBytes(new string([.. Enumerable.Range(0, Math.Max(size, 1)).Select(i => (char)('a' + (i % 26)))]));
        var tasks = new List<Task<Hop>>();
        for (var ttl = 1; ttl <= limit; ttl++)
        {
            tasks.Add(Probe(ip, ttl, payload));
            if (ttl < limit)
            {
                await Task.Delay(StaggerMs, token);
            }
        }

        var hops = (await Task.WhenAll(tasks)).ToList();
        _lastAnswer = hops.LastOrDefault(h => h.Addr is not null)?.Ttl ?? 0;

        // cut the list at the first hop where the target itself answered
        var reached = hops.FirstOrDefault(h => h.Addr == ip.ToString() && h.Status is "reply" or "unreachable")?.Ttl ?? 0;
        if (reached > 0)
        {
            hops = hops[..reached];
            _destHop = reached;
        }
        else if (_destHop > 0 && limit < maxHops)
        {
            _destHop = 0; // route got longer or target is down: search the full range next time
        }

        foreach (var hop in hops.Where(h => h.Addr is not null && _resolved.Add(h.Addr)))
        {
            _ = Lookup(hop.Addr!);
        }

        return new Round(number, start, [.. hops]);
    }

    private async Task<Hop> Probe(IPAddress ip, int ttl, byte[] payload)
    {
        using var ping = new Ping();
        try
        {
            // .NET reports RoundtripTime only for echo replies (0 for TTL expired), so time it here
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var reply = await ping.SendPingAsync(ip, timeoutMs, payload, new PingOptions(ttl, true));
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var status = reply.Status switch
            {
                IPStatus.Success => "reply",
                IPStatus.TtlExpired or IPStatus.TimeExceeded => "ttl-expired",
                IPStatus.TimedOut => "timeout",
                IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable or IPStatus.DestinationPortUnreachable or IPStatus.DestinationProtocolUnreachable or IPStatus.DestinationUnreachable => "unreachable",
                var other => other.ToString(),
            };
            var answered = reply.Address is { } a && !a.Equals(IPAddress.Any) && status != "timeout";
            var rtt = reply.Status == IPStatus.Success && reply.RoundtripTime > 0 ? reply.RoundtripTime : elapsed;
            return new Hop(ttl, answered ? reply.Address!.ToString() : null, answered ? Math.Round(Math.Max(rtt, 0.1), 1) : null, status);
        }
        catch (PingException)
        {
            return new Hop(ttl, null, null, "error");
        }
    }

    private async Task Lookup(string addr)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var entry = await Dns.GetHostEntryAsync(addr, cts.Token);
            if (!string.IsNullOrEmpty(entry.HostName) && entry.HostName != addr)
            {
                HostResolved?.Invoke(addr, entry.HostName.TrimEnd('.'));
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            // no reverse name
        }
    }
}

public sealed record HopRow(int Ttl, string? Addr, Dictionary<string, int> Addrs, int Sent, int Recv, double Loss, double? Last, double? Best, double? Avg, double? Worst, double? Stdev, double? Jitter, double?[] Rtts);

/// <summary>WinMTR-style statistics per hop.</summary>
public sealed class Aggregator
{
    private const int KeepRtts = 300;
    private readonly List<Acc> _hops = [];

    public int Rounds { get; private set; }

    public int Length { get; private set; }

    public void Add(Round round)
    {
        Rounds++;
        Length = round.Hops.Length;
        foreach (var hop in round.Hops)
        {
            while (_hops.Count < hop.Ttl)
            {
                _hops.Add(new Acc());
            }

            var acc = _hops[hop.Ttl - 1];
            acc.Sent++;
            if (hop.Addr is not null && hop.Rtt is { } rtt)
            {
                acc.Addrs[hop.Addr] = acc.Addrs.GetValueOrDefault(hop.Addr) + 1;
                acc.Recv++;
                if (acc.Last is { } last)
                {
                    acc.JitterSum += Math.Abs(rtt - last);
                    acc.JitterCount++;
                }

                acc.Last = rtt;
                acc.Best = Math.Min(acc.Best ?? rtt, rtt);
                acc.Worst = Math.Max(acc.Worst ?? rtt, rtt);
                acc.Sum += rtt;
                acc.SumSq += rtt * rtt;
                acc.Rtts.Add(rtt);
            }
            else
            {
                acc.Rtts.Add(null);
            }

            if (acc.Rtts.Count > KeepRtts)
            {
                acc.Rtts.RemoveAt(0);
            }
        }
    }

    public HopRow[] Rows() => [.. _hops.Take(Length).Select((a, i) =>
    {
        double? avg = a.Recv > 0 ? a.Sum / a.Recv : null;
        double? variance = avg is { } m ? (a.SumSq / a.Recv) - (m * m) : null;
        return new HopRow(
            i + 1,
            a.Addrs.OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault(),
            new Dictionary<string, int>(a.Addrs),
            a.Sent,
            a.Recv,
            a.Sent > 0 ? (a.Sent - a.Recv) * 100.0 / a.Sent : 0,
            a.Last,
            a.Best,
            avg,
            a.Worst,
            variance is { } v ? Math.Sqrt(Math.Max(0, v)) : null,
            a.JitterCount > 0 ? a.JitterSum / a.JitterCount : null,
            [.. a.Rtts]);
    })];

    private sealed class Acc
    {
        public Dictionary<string, int> Addrs { get; } = [];

        public int Sent { get; set; }

        public int Recv { get; set; }

        public double? Last { get; set; }

        public double? Best { get; set; }

        public double? Worst { get; set; }

        public double Sum { get; set; }

        public double SumSq { get; set; }

        public double JitterSum { get; set; }

        public int JitterCount { get; set; }

        public List<double?> Rtts { get; } = [];
    }
}

/// <summary>Compact per-check record stored by the background monitor.</summary>
public sealed record Sample(DateTimeOffset T, string Ip, int Rounds, double Loss, double? Avg, double? Best, double? Worst, double? Jitter, bool Reached, HopSample[] Hops, string Route);

public sealed record HopSample(string? Addr, double Loss, double? Avg);

public static class Stats
{
    private const string Bars = "▁▂▃▄▅▆▇█";

    public static Sample Summarize(string ip, IReadOnlyList<Round> rounds)
    {
        var agg = new Aggregator();
        foreach (var r in rounds)
        {
            agg.Add(r);
        }

        var rows = agg.Rows();
        var last = rows.LastOrDefault();
        var reached = last?.Addr == ip;
        return new Sample(
            rounds.FirstOrDefault()?.Time ?? DateTimeOffset.Now,
            ip,
            agg.Rounds,
            reached ? Math.Round(last!.Loss, 1) : 100,
            reached ? R(last!.Avg) : null,
            reached ? last!.Best : null,
            reached ? last!.Worst : null,
            reached ? R(last!.Jitter) : null,
            reached,
            [.. rows.Select(r => new HopSample(r.Addr, R(r.Loss) ?? 0, R(r.Avg)))],
            RouteSignature(rows.Select(r => r.Addr)));
    }

    public static string RouteSignature(IEnumerable<string?> addrs) => string.Join(">", addrs.Select(a => a ?? "*"));

    /// <summary>True if two routes differ at a hop where both sides answered, or in length.</summary>
    public static bool RoutesDiffer(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        var x = a.Split('>');
        var y = b.Split('>');
        return x.Length != y.Length || x.Where((addr, i) => addr != "*" && y[i] != "*" && addr != y[i]).Any();
    }

    public static string Ms(double? value) => value is not { } v ? "–" : v < 10 ? $"{v:0.0} ms" : $"{Math.Round(v)} ms";

    public static string Pct(double value) => value is > 0 and < 10 ? $"{value:0.0}%" : $"{Math.Round(value)}%";

    public static Microsoft.CommandPalette.Extensions.Toolkit.Tag LossTag(double loss) =>
        loss >= 20 ? Core.Ui.Red(Pct(loss)) : loss > 0 ? Core.Ui.Orange(Pct(loss)) : Core.Ui.Green(Pct(loss));

    /// <summary>Text sparkline; lost probes are shown as "×".</summary>
    public static string Sparkline(IEnumerable<double?> values, int width)
    {
        var points = values.TakeLast(width).ToList();
        var known = points.Where(v => v is not null).Select(v => v!.Value).ToList();
        if (known.Count == 0)
        {
            return new string('×', points.Count);
        }

        var low = known.Min();
        var high = known.Max();
        return string.Concat(points.Select(v => v is not { } x ? '×' : high - low < 1e-9 ? Bars[1] : Bars[(int)Math.Round((x - low) / (high - low) * (Bars.Length - 1))]));
    }

    /// <summary>WinMTR-like plain text table.</summary>
    public static string AsTable(string target, HopRow[] rows, IReadOnlyDictionary<string, string> hosts)
    {
        string N(double? v) => v is not { } x ? "-" : x < 10 ? x.ToString("0.0") : Math.Round(x).ToString();
        string[] header = ["#", "Host", "Loss%", "Sent", "Recv", "Best", "Avg", "Worst", "Last", "StDev"];
        var lines = rows.Select(r => new[]
        {
            r.Ttl.ToString(),
            r.Addr is null ? "???" : hosts.TryGetValue(r.Addr, out var n) ? $"{n} ({r.Addr})" : r.Addr,
            r.Loss.ToString("0.0"), r.Sent.ToString(), r.Recv.ToString(), N(r.Best), N(r.Avg), N(r.Worst), N(r.Last), N(r.Stdev),
        }).ToList();
        var widths = header.Select((h, i) => Math.Max(h.Length, lines.Select(l => l[i].Length).DefaultIfEmpty(0).Max())).ToArray();
        string Fmt(string[] cells) => string.Join("  ", cells.Select((c, i) => i == 1 ? c.PadRight(widths[i]) : c.PadLeft(widths[i])));
        return string.Join("\n", [$"MTR to {target}", Fmt(header), .. lines.Select(Fmt)]);
    }

    private static double? R(double? v) => v is { } x ? Math.Round(x, 1) : null;
}
