using System.Runtime.InteropServices;
using System.Text.Json;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.LauncherKey;

/// <summary>
/// Low-level keyboard hook that turns a short tap of the Windows key (no other key in between) into the
/// Command Palette hotkey. Win + another key and a long press keep their normal meaning.
/// </summary>
internal static partial class WinKeyHook
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;
    private const int WmQuit = 0x0012;
    private const uint LlkhfInjected = 0x10;
    private const ushort VkLwin = 0x5B;
    private const ushort VkRwin = 0x5C;
    private const ushort VkMenu = 0x12;
    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;

    // Unassigned virtual key: pressing it while Win is down stops the Start menu from opening
    private const ushort VkUnassigned = 0xE8;

    private static readonly HookProc Callback = OnKey;
    private static uint _threadId;
    private static bool _winDown;
    private static bool _otherKey;
    private static uint _downTime;

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    public static bool Running => _threadId != 0;

    /// <summary>Longest press in milliseconds that still counts as a tap.</summary>
    public static int MaxTapMs { get; set; } = 400;

    public static void Start()
    {
        if (Running)
        {
            return;
        }

        var ready = new ManualResetEventSlim();
        new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var hook = SetWindowsHookExW(WhKeyboardLl, Callback, GetModuleHandleW(null), 0);
            ready.Set();
            if (hook == 0)
            {
                Log.Error($"launcher key: SetWindowsHookEx failed ({Marshal.GetLastPInvokeError()})");
                _threadId = 0;
                return;
            }

            Log.Info("launcher key: hook installed");
            while (GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            UnhookWindowsHookEx(hook);
            _threadId = 0;
            Log.Info("launcher key: hook removed");
        })
        { IsBackground = true, Name = "LauncherKeyHook" }.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    public static void Stop()
    {
        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, WmQuit, 0, 0);
        }
    }

    private static nint OnKey(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if ((key.Flags & LlkhfInjected) == 0 && Handle(key, (int)wParam))
            {
                return 1; // swallowed
            }
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    private static bool Handle(KbdLlHookStruct key, int message)
    {
        var down = message is WmKeydown or WmSyskeydown;
        var up = message is WmKeyup or WmSyskeyup;
        var isWin = key.Vk is VkLwin or VkRwin;

        if (!isWin)
        {
            if (down && _winDown)
            {
                _otherKey = true;
            }

            return false;
        }

        if (down)
        {
            if (!_winDown)
            {
                _winDown = true;
                _otherKey = false;
                _downTime = key.Time;

                // Mask the press right away: the unassigned key is queued behind this Win-down, so Windows
                // sees "Win + another key" and never opens the Start menu on release. Nothing is swallowed,
                // the real Win-up still reaches Windows and every other hook (no stuck Win key).
                Native.SendKeys(VkUnassigned);
            }

            return false;
        }

        if (!up || !_winDown)
        {
            return false;
        }

        _winDown = false;
        if (!_otherKey && unchecked(key.Time - _downTime) <= MaxTapMs)
        {
            // Opened after the real Win-up has been processed; one SendInput batch cannot be interleaved
            // with what the user types next
            Opener.Add(PaletteHotkey);
        }

        return false;
    }

    // Single worker so hotkey presses never overlap or wait for the thread pool
    private static readonly System.Collections.Concurrent.BlockingCollection<Func<ushort[]>> Opener = StartOpener();

    private static System.Collections.Concurrent.BlockingCollection<Func<ushort[]>> StartOpener()
    {
        var queue = new System.Collections.Concurrent.BlockingCollection<Func<ushort[]>>();
        new Thread(() =>
        {
            foreach (var keys in queue.GetConsumingEnumerable())
            {
                Native.SendKeys(keys());
            }
        })
        { IsBackground = true, Name = "LauncherKeyOpener" }.Start();
        return queue;
    }

    /// <summary>The activation hotkey configured in Command Palette (default Win+Alt+Space).</summary>
    internal static ushort[] PaletteHotkey()
    {
        try
        {
            var file = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages",
                "Microsoft.CommandPalette_8wekyb3d8bbwe",
                "LocalState",
                "settings.json");
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            var hotkey = json.RootElement.GetProperty("Hotkey");
            var keys = new List<ushort>();
            if (hotkey.GetProperty("win").GetBoolean())
            {
                keys.Add(VkLwin);
            }

            if (hotkey.GetProperty("ctrl").GetBoolean())
            {
                keys.Add(VkControl);
            }

            if (hotkey.GetProperty("alt").GetBoolean())
            {
                keys.Add(VkMenu);
            }

            if (hotkey.GetProperty("shift").GetBoolean())
            {
                keys.Add(VkShift);
            }

            keys.Add((ushort)hotkey.GetProperty("code").GetInt32());
            return [.. keys];
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [VkLwin, VkMenu, 0x20];
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint Vk;
        public uint Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowsHookExW(int id, HookProc proc, nint module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Msg msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref Msg msg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(ref Msg msg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint msg, nint wParam, nint lParam);
}
