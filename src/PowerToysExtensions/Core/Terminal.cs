using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PowerToysExtensions.Core;

/// <summary>Opens commands (ssh, sftp …) in Windows Terminal, PowerShell or cmd.</summary>
public static partial class Terminal
{
    private static readonly Dictionary<string, string> WtPackages = new()
    {
        ["wtpreview"] = "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe",
        ["wtcanary"] = "Microsoft.WindowsTerminalCanary_8wekyb3d8bbwe",
    };

    public static List<Microsoft.CommandPalette.Extensions.Toolkit.ChoiceSetSetting.Choice> Choices =>
    [
        new("Windows Terminal", "auto"),
        new("Windows Terminal Preview", "wtpreview"),
        new("Windows Terminal Canary", "wtcanary"),
        new("PowerShell window", "powershell"),
        new("Command Prompt window", "cmd"),
    ];

    /// <summary>Only allow characters that are safe to pass through to terminals and shells.</summary>
    public static string SafeArg(string value) =>
        SafeArgRegex().IsMatch(value) ? value : throw new InvalidOperationException($"Unsupported characters in \"{value}\"");

    public static void Open(string title, IReadOnlyList<string> command, string choice = "auto", string? wtProfile = null, bool newWindow = false)
    {
        if (choice == "powershell")
        {
            Start("cmd.exe", ["/c", "start", string.Empty, "powershell.exe", "-NoExit", "-Command", .. command]);
            return;
        }

        if (choice == "cmd")
        {
            Start("cmd.exe", ["/c", "start", string.Empty, "cmd.exe", "/k", .. command]);
            return;
        }

        // --suppressApplicationTitle keeps the given title; remote shells would otherwise set "user@host: ~"
        var args = new List<string> { "-w", newWindow ? "new" : "0", "new-tab", "--title", title, "--suppressApplicationTitle" };
        if (!string.IsNullOrWhiteSpace(wtProfile))
        {
            args.AddRange(["--profile", wtProfile.Trim()]);
        }

        try
        {
            Start(WindowsTerminal(choice), [.. args, .. command]);
            _ = Focus(title);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Windows Terminal not installed: plain console window
            Start("cmd.exe", ["/c", "start", string.Empty, .. command]);
        }
    }

    /// <summary>
    /// Windows' focus-stealing protection keeps an already running Windows Terminal in the background when a
    /// background process adds a tab; bring the window with the tab's title to the front.
    /// </summary>
    public static async Task Focus(string title)
    {
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(100);
            var window = FindWindow(title);
            if (window == 0)
            {
                continue;
            }

            // a tap of Alt lets this process set the foreground window
            Native.SendKeys(0x12);
            ShowWindow(window, 9);
            SetForegroundWindow(window);
            return;
        }
    }

    private static string WindowsTerminal(string choice)
    {
        if (!WtPackages.TryGetValue(choice, out var package))
        {
            return "wt.exe";
        }

        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", package, "wt.exe");

        // app execution aliases are reparse points: File.Exists fails, but the directory entry is there
        return new FileInfo(exe).Attributes != (FileAttributes)(-1) && Directory.Exists(Path.GetDirectoryName(exe))
            ? exe
            : throw new InvalidOperationException($"{(choice == "wtpreview" ? "Windows Terminal Preview" : "Windows Terminal Canary")} is not installed");
    }

    private static void Start(string exe, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        Process.Start(info)?.Dispose();
    }

    private static nint FindWindow(string title)
    {
        nint found = 0;
        EnumWindows(
            (hwnd, _) =>
            {
                var buffer = new StringBuilder(512);
                GetWindowTextW(hwnd, buffer, buffer.Capacity);
                if (IsWindowVisible(hwnd) && buffer.ToString().Contains(title, StringComparison.Ordinal))
                {
                    found = hwnd;
                    return false;
                }

                return true;
            },
            0);
        return found;
    }

    [GeneratedRegex(@"^[\w.@:%+=,/~\\-]+$")]
    private static partial Regex SafeArgRegex();

    private delegate bool EnumProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumProc callback, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
}
