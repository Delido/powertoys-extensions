using System.Runtime.InteropServices;
using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.LauncherKey;

/// <summary>
/// Low-level keyboard hook that turns a short tap of the Windows key (no other key in between) into
/// opening Command Palette. Win + another key and a long press keep their normal meaning.
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
            }

            return false;
        }

        if (!up || !_winDown)
        {
            return false;
        }

        _winDown = false;
        if (_otherKey || unchecked(key.Time - _downTime) > MaxTapMs)
        {
            return false;
        }

        // A tap of Win alone: swallow this Win-up and queue, right here in the hook, one batch of
        // "unassigned key down, up, Win up". Windows then sees Win + another key (no Start menu), and the
        // batch is queued before anything the user types next, so Win can never stay logically pressed.
        Native.SendInput(3, [Key(VkUnassigned, false), Key(VkUnassigned, true), Key((ushort)key.Vk, true)], Marshal.SizeOf<Native.Input>());
        Opener.Add(true);
        return true;
    }

    private static Native.Input Key(ushort vk, bool up) => new()
    {
        Type = Native.InputKeyboard,
        Keyboard = new Native.KeyboardInput
        {
            Vk = vk,
            Flags = (up ? Native.KeyeventfKeyup : 0) | (vk is VkLwin or VkRwin ? Native.KeyeventfExtendedkey : 0),
        },
    };

    // Activating Command Palette like a second launch makes it summon its window – no synthetic hotkey,
    // so nothing is typed on the user's behalf. A single worker keeps it off the hook thread.
    private static readonly System.Collections.Concurrent.BlockingCollection<bool> Opener = StartOpener();

    private static System.Collections.Concurrent.BlockingCollection<bool> StartOpener()
    {
        var queue = new System.Collections.Concurrent.BlockingCollection<bool>();
        new Thread(() =>
        {
            foreach (var request in queue.GetConsumingEnumerable())
            {
                try
                {
                    var manager = (IApplicationActivationManager)new ApplicationActivationManager();
                    manager.ActivateApplication(PaletteAppId, null, 0, out _);
                }
                catch (Exception ex)
                {
                    Log.Error("launcher key: activating Command Palette failed", ex);
                }
            }
        })
        { IsBackground = true, Name = "LauncherKeyOpener" }.Start();
        return queue;
    }

    private const string PaletteAppId = "Microsoft.CommandPalette_8wekyb3d8bbwe!App";

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);

        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, nint itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);

        int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, nint itemArray, out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager
    {
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
