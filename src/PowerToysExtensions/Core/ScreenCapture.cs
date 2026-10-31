using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PowerToysExtensions.Core;

/// <summary>Captures all monitors as a PNG.</summary>
public static partial class ScreenCapture
{
    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;
    private const uint SrcCopy = 0x00CC0020, CaptureBlt = 0x40000000;
    private static readonly nint PerMonitorAwareV2 = -4;

    /// <summary>The whole virtual screen, scaled down so the longer side is at most <paramref name="maxSide"/> pixels.</summary>
    public static async Task<byte[]> Png(int maxSide = 2048)
    {
        var (width, height, pixels) = Capture();
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(width, height));
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
        if (scale < 1)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)(width * scale);
            encoder.BitmapTransform.ScaledHeight = (uint)(height * scale);
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        await stream.GetInputStreamAt(0).ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    private static (int Width, int Height, byte[] Pixels) Capture()
    {
        // per-monitor aware, otherwise scaled displays are captured only partly
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        nint bitmap = 0;
        try
        {
            int x = GetSystemMetrics(SmXVirtualScreen), y = GetSystemMetrics(SmYVirtualScreen);
            int width = GetSystemMetrics(SmCxVirtualScreen), height = GetSystemMetrics(SmCyVirtualScreen);
            bitmap = CreateCompatibleBitmap(screen, width, height);
            var old = SelectObject(memory, bitmap);
            BitBlt(memory, 0, 0, width, height, screen, x, y, SrcCopy | CaptureBlt);
            SelectObject(memory, old);

            var header = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            var pixels = new byte[width * height * 4];
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref header, 0) == 0)
            {
                throw new InvalidOperationException("Screen capture failed");
            }

            return (width, height, pixels);
        }
        finally
        {
            if (bitmap != 0)
            {
                DeleteObject(bitmap);
            }

            DeleteDC(memory);
            _ = ReleaseDC(0, screen);
            SetThreadDpiAwarenessContext(previous);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint dc, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);
}
