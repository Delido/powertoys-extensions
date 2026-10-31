using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PowerToysExtensions.Core;

/// <summary>Hands secrets (LAPS passwords …) to the user without leaving traces.</summary>
public static partial class Secrets
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    /// <summary>
    /// Puts the secret on the clipboard, excluded from the clipboard history and the cloud clipboard, and
    /// clears it after the given time if it still holds the secret.
    /// </summary>
    public static void CopyToClipboard(string secret, TimeSpan clearAfter)
    {
        SetClipboard(secret, exclude: true);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        _ = Task.Delay(clearAfter).ContinueWith(_ =>
        {
            if (GetClipboard() is { } current && SHA256.HashData(Encoding.UTF8.GetBytes(current)).AsSpan().SequenceEqual(hash))
            {
                SetClipboard(null, exclude: false);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Starts Remote Desktop with credentials stored for TERMSRV/host only for 20 seconds.</summary>
    public static void StartRdp(string address, string user, string password)
    {
        var host = address.Contains(':', StringComparison.Ordinal) ? address[..address.LastIndexOf(':')] : address;
        var target = $"TERMSRV/{host}";
        var blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var credential = new Credential
            {
                Type = 1, // CRED_TYPE_GENERIC
                TargetName = target,
                UserName = user,
                Persist = 1, // CRED_PERSIST_SESSION
                CredentialBlobSize = password.Length * 2,
                CredentialBlob = blob,
            };
            if (!CredWriteW(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }

        Process.Start(new ProcessStartInfo("mstsc.exe", $"/v:{address}") { UseShellExecute = false })?.Dispose();
        _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => CredDeleteW(target, 1, 0), TaskScheduler.Default);
    }

    private static void SetClipboard(string? text, bool exclude)
    {
        for (var attempt = 0; attempt < 10 && !OpenClipboard(0); attempt++)
        {
            Thread.Sleep(50);
        }

        try
        {
            EmptyClipboard();
            if (text is null)
            {
                return;
            }

            SetData(CfUnicodeText, Encoding.Unicode.GetBytes(text + "\0"));
            if (exclude)
            {
                byte[] zero = [0, 0, 0, 0];
                SetData(RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing"), zero);
                SetData(RegisterClipboardFormatW("CanIncludeInClipboardHistory"), zero);
                SetData(RegisterClipboardFormatW("CanUploadToCloudClipboard"), zero);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void SetData(uint format, byte[] data)
    {
        var handle = GlobalAlloc(GmemMoveable, (nuint)data.Length);
        var target = GlobalLock(handle);
        Marshal.Copy(data, 0, target, data.Length);
        GlobalUnlock(handle);
        SetClipboardData(format, handle);
    }

    private static string? GetClipboard()
    {
        if (!OpenClipboard(0))
        {
            return null;
        }

        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == 0)
            {
                return null;
            }

            var pointer = GlobalLock(handle);
            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref Credential credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    private static partial nint SetClipboardData(uint format, nint data);

    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint handle);
}
