using System.Runtime.InteropServices;

namespace PowerToysExtensions.Core;

/// <summary>The classic Windows "Open" dialog (Command Palette forms have no file picker).</summary>
public static class FileDialog
{
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnNoChangeDir = 0x00000008;
    private const int OfnExplorer = 0x00080000;

    /// <summary>Shows the dialog on its own STA thread; returns the chosen file or null.</summary>
    public static Task<string?> Open(string title, string filter = "All files\0*.*\0", string? folder = null)
    {
        var result = new TaskCompletionSource<string?>();
        var thread = new Thread(() =>
        {
            var buffer = new char[4096];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var ofn = new OpenFileName
                {
                    StructSize = Marshal.SizeOf<OpenFileName>(),
                    Filter = filter,
                    File = handle.AddrOfPinnedObject(),
                    MaxFile = buffer.Length,
                    Title = title,
                    InitialDir = folder,
                    Flags = OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir | OfnExplorer,
                };
                result.SetResult(GetOpenFileNameW(ref ofn) ? new string(buffer, 0, Array.IndexOf(buffer, '\0')) : null);
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
            finally
            {
                handle.Free();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public nint Owner;
        public nint Instance;
        public string Filter;
        public string? CustomFilter;
        public int MaxCustFilter;
        public int FilterIndex;
        public nint File;
        public int MaxFile;
        public string? FileTitle;
        public int MaxFileTitle;
        public string? InitialDir;
        public string Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public string? DefExt;
        public nint CustData;
        public nint Hook;
        public string? TemplateName;
        public nint Reserved;
        public int ReservedInt;
        public int FlagsEx;
    }
}
