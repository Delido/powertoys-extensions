using System.Runtime.InteropServices;

namespace PowerToysExtensions.Core;

/// <summary>Shows and hides the Command Palette window from an extension.</summary>
public static partial class Palette
{
    private const string AppId = "Microsoft.CommandPalette_8wekyb3d8bbwe!App";
    private const uint WmHotkey = 0x0312;

    /// <summary>The palette window if it is the foreground window.</summary>
    public static nint? InForeground()
    {
        var window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out var pid) == 0)
        {
            return null;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName.Equals("Microsoft.CmdPal.UI", StringComparison.OrdinalIgnoreCase) ? window : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Command Palette toggles on its own root hotkey (index 0), so it gets that message instead of a typed hotkey.</summary>
    public static void Toggle(nint window) => PostMessageW(window, WmHotkey, 0, 0);

    /// <summary>Activating Command Palette like a second launch makes it summon its window – nothing is typed.</summary>
    public static void Summon()
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        manager.ActivateApplication(AppId, null, 0, out _);
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

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
}
