using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PowerToysExtensions.Extensions.PowerPlan;

public sealed record Plan(string Guid, string Name);

public sealed record Proc(int Pid, string Name, string? Path);

public sealed record Foreground(int Pid, string? Name, string? Path, string Title, string Class, bool Fullscreen);

/// <summary>Power plans, running processes and the foreground window (no administrator rights needed).</summary>
public static partial class PowerApi
{
    // Well-known GUIDs of the Windows default plans
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";
    public const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    private const uint AccessScheme = 16;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public static Plan[] Plans()
    {
        var plans = new List<Plan>();
        for (uint index = 0; ; index++)
        {
            var guid = default(Guid);
            var size = (uint)Marshal.SizeOf<Guid>();
            if (PowerEnumerate(0, 0, 0, AccessScheme, index, ref guid, ref size) != 0)
            {
                break;
            }

            plans.Add(new Plan(guid.ToString("D"), FriendlyName(guid)));
        }

        return [.. plans];
    }

    public static string Active()
    {
        if (PowerGetActiveScheme(0, out var ptr) != 0 || ptr == 0)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(ptr).ToString("D");
        }
        finally
        {
            LocalFree(ptr);
        }
    }

    public static void SetActive(string guid)
    {
        var g = Guid.Parse(guid);
        var result = PowerSetActiveScheme(0, ref g);
        if (result != 0)
        {
            throw new InvalidOperationException($"PowerSetActiveScheme failed ({result})");
        }
    }

    public static string NameOf(Plan[] plans, string? guid) => plans.FirstOrDefault(p => p.Guid == guid)?.Name ?? "Unknown plan";

    public static Proc[] Processes()
    {
        var list = new List<Proc>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == 0)
                {
                    continue;
                }

                list.Add(new Proc(p.Id, $"{p.ProcessName}.exe", PathOf(p.Id)));
            }
        }

        return [.. list];
    }

    public static string? PathOf(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var buffer = new char[32768];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static Foreground? GetForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0)
        {
            return null;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        var path = PathOf((int)pid);
        var cls = Text(hwnd, true);
        var title = Text(hwnd, false);
        var name = path is null ? null : System.IO.Path.GetFileName(path);

        // The desktop and shell windows cover the whole screen but aren't apps. A maximized window
        // overlaps the screen edges by its border (looks like fullscreen with an auto-hiding taskbar).
        var fullscreen = false;
        if (cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd") && !IsZoomed(hwnd) && GetWindowRect(hwnd, out var wr))
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref info))
            {
                // borderless and exclusive fullscreen match the monitor exactly
                fullscreen = wr.Equals(info.Monitor);
            }
        }

        return new Foreground((int)pid, name, path, title, cls, fullscreen);
    }

    private static string FriendlyName(Guid guid)
    {
        uint size = 0;
        PowerReadFriendlyName(0, ref guid, 0, 0, null, ref size);
        if (size == 0)
        {
            return string.Empty;
        }

        var buffer = new byte[size];
        return PowerReadFriendlyName(0, ref guid, 0, 0, buffer, ref size) == 0 ? Encoding.Unicode.GetString(buffer).TrimEnd('\0') : string.Empty;
    }

    private static string Text(nint hwnd, bool className)
    {
        var buffer = new char[256];
        var n = className ? GetClassNameW(hwnd, buffer, buffer.Length) : GetWindowTextW(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, n));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect : IEquatable<Rect>
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly bool Equals(Rect other) => Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;

        public override readonly bool Equals(object? obj) => obj is Rect r && Equals(r);

        public override readonly int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerEnumerate(nint root, nint schemeGuid, nint subGroup, uint accessFlags, uint index, ref Guid buffer, ref uint size);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadFriendlyName(nint root, ref Guid scheme, nint subGroup, nint setting, [Out] byte[]? buffer, ref uint size);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(nint root, out nint activeScheme);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveScheme(nint root, ref Guid scheme);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint mem);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] name, ref uint size);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hwnd, [Out] char[] name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hwnd, [Out] char[] text, int max);
}
