using System.Text.Json;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.KeepAwake;

public sealed record AwakeState(DateTime StartedAt, DateTime? Until, bool Teams, bool Display);

/// <summary>
/// Holds the system awake on a dedicated thread (an execution state belongs to the thread that set it).
/// The state is saved, so Keep Awake continues after Command Palette restarts.
/// </summary>
public static class AwakeEngine
{
    private const ushort VkF15 = 0x7E;
    private static readonly Lock Gate = new();
    private static readonly string StateFile = Path.Combine(Paths.For("keep-awake"), "state.json");
    private static CancellationTokenSource? _stop;

    public static event Action? Changed;

    public static AwakeState? Current { get; private set; }

    /// <summary>Starts Keep Awake (replacing a running one). duration null = indefinitely.</summary>
    public static void Start(TimeSpan? duration, bool teams, bool display)
    {
        Run(new AwakeState(DateTime.Now, duration is { } d ? DateTime.Now + d : null, teams, display));
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _stop?.Cancel();
            _stop = null;
            Current = null;
            File.Delete(StateFile);
        }

        Changed?.Invoke();
    }

    /// <summary>Continues a Keep Awake that was running when the process ended.</summary>
    public static void Resume()
    {
        try
        {
            if (Current is null && File.Exists(StateFile) &&
                JsonSerializer.Deserialize<AwakeState>(File.ReadAllText(StateFile)) is { } state &&
                (state.Until is null || state.Until > DateTime.Now))
            {
                Run(state);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Error("keep-awake resume", ex);
        }
    }

    public static string Describe(AwakeState state)
    {
        if (state.Until is not { } until)
        {
            return "indefinitely";
        }

        return until.Date == DateTime.Today ? $"until {until:HH:mm}" : $"until {until:ddd HH:mm}";
    }

    private static void Run(AwakeState state)
    {
        CancellationTokenSource stop;
        lock (Gate)
        {
            _stop?.Cancel();
            _stop = stop = new CancellationTokenSource();
            Current = state;
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        }

        new Thread(() => Loop(state, stop.Token)) { IsBackground = true, Name = "KeepAwake" }.Start();
        Changed?.Invoke();
    }

    private static void Loop(AwakeState state, CancellationToken token)
    {
        var flags = Native.EsContinuous | Native.EsSystemRequired | (state.Display ? Native.EsDisplayRequired : 0);
        Native.SetThreadExecutionState(flags);
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (state.Until is { } until && DateTime.Now >= until)
                {
                    Stop();
                    break;
                }

                // F15 is unused but counts as input, so Teams & co. do not switch to Away
                if (state.Teams && Native.IdleSeconds() >= 60)
                {
                    Native.SendKeys(VkF15);
                }

                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            Native.SetThreadExecutionState(Native.EsContinuous);
        }
    }
}
