using System.Runtime.InteropServices;

namespace PowerToysExtensions.Core;

/// <summary>Win32 functions shared by several extensions.</summary>
internal static partial class Native
{
    public const uint EsContinuous = 0x80000000;
    public const uint EsSystemRequired = 0x00000001;
    public const uint EsDisplayRequired = 0x00000002;

    public const int InputKeyboard = 1;
    public const uint KeyeventfKeyup = 0x0002;
    public const uint KeyeventfExtendedkey = 0x0001;

    [LibraryImport("kernel32.dll")]
    public static partial uint SetThreadExecutionState(uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    /// <summary>Seconds since the last keyboard or mouse input of the user.</summary>
    public static uint IdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Time) / 1000 : 0;
    }

    /// <summary>Presses and releases the given keys (all down in order, then all up in reverse order).</summary>
    public static void SendKeys(params ushort[] keys)
    {
        var inputs = new Input[keys.Length * 2];
        for (var i = 0; i < keys.Length; i++)
        {
            inputs[i] = Key(keys[i], up: false);
            inputs[(keys.Length * 2) - 1 - i] = Key(keys[i], up: true);
        }

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static Input Key(ushort vk, bool up) => new()
    {
        Type = InputKeyboard,
        Keyboard = new KeyboardInput
        {
            Vk = vk,
            Flags = (up ? KeyeventfKeyup : 0) | (IsExtended(vk) ? KeyeventfExtendedkey : 0),
        },
    };

    // Windows keys, arrows etc. must be sent as extended keys or Windows treats them as numpad keys
    private static bool IsExtended(ushort vk) => vk is 0x5B or 0x5C or (>= 0x21 and <= 0x2E);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public int Type;
        public KeyboardInput Keyboard;

        // the union in INPUT is as large as MOUSEINPUT; pad so SendInput accepts the size
        private readonly long _pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}
