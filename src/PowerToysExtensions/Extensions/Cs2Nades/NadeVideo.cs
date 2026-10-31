using PowerToysExtensions.Core;

namespace PowerToysExtensions.Extensions.Cs2Nades;

/// <summary>
/// Turns a lineup video into an animated GIF with ffmpeg, because Command Palette shows GIFs in markdown
/// but cannot play videos. GIFs are cached (500 MB at most, oldest removed first).
/// </summary>
public static class NadeVideo
{
    private const long CacheLimit = 500L * 1024 * 1024;

    // 10 fps and 480 px keep a 17 s clip around 5 MB and the conversion around 5 s
    private const string Filter =
        "fps=10,scale=480:-2:flags=bicubic,split[a][b];[a]palettegen=max_colors=96:stats_mode=diff[p];" +
        "[b][p]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle";

    private static readonly string CacheDir = Paths.For(Path.Combine("cs2-nades", "videos"));
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string WingetPackages = Path.Combine(Home, "AppData", "Local", "Microsoft", "WinGet", "Packages");

    public static string? FindFfmpeg(string custom)
    {
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return File.Exists(custom.Trim()) ? custom.Trim() : null;
        }

        var found = Proc.Find(
            "ffmpeg.exe",
            Path.Combine(Home, "AppData", "Local", "Microsoft", "WinGet", "Links"),
            @"C:\ProgramData\chocolatey\bin",
            Path.Combine(Home, "scoop", "shims"));
        if (found is not null || !Directory.Exists(WingetPackages))
        {
            return found;
        }

        // winget's portable FFmpeg packages (Gyan builds)
        return Directory.EnumerateDirectories(WingetPackages, "Gyan.FFmpeg*")
            .SelectMany(Directory.EnumerateDirectories)
            .Select(build => Path.Combine(build, "bin", "ffmpeg.exe"))
            .FirstOrDefault(File.Exists);
    }

    public static async Task InstallFfmpeg()
    {
        var winget = Path.Combine(Home, "AppData", "Local", "Microsoft", "WindowsApps", "winget.exe");
        var result = await Proc.Run(
            winget,
            ["install", "--id", "Gyan.FFmpeg.Essentials", "-e", "--silent", "--disable-interactivity", "--accept-source-agreements", "--accept-package-agreements"],
            TimeSpan.FromMinutes(10));

        // winget exits non-zero when the package is already installed
        if (result.ExitCode != 0 && !result.Output.Contains("installed", StringComparison.OrdinalIgnoreCase) && !result.Output.Contains("installiert", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(result.Output.Trim().Split('\n').LastOrDefault() ?? $"winget exited with {result.ExitCode}");
        }
    }

    public static string? Cached(Nade nade)
    {
        var file = GifPath(nade);
        return File.Exists(file) ? file : null;
    }

    public static async Task<string> Make(Nade nade, string ffmpeg, CancellationToken token)
    {
        var file = GifPath(nade);
        var tmp = $"{file}.{Environment.TickCount64}.part";

        // The short preview clip has the whole throw and is a third of the size of the LQ video
        var source = nade.VideoLq.EndsWith("lq.mp4", StringComparison.Ordinal) ? nade.VideoLq[..^"lq.mp4".Length] + "thumbnail.mp4" : nade.VideoLq;
        try
        {
            var result = await Proc.Run(ffmpeg, ["-v", "error", "-y", "-i", source, "-vf", Filter, "-loop", "0", "-f", "gif", tmp], TimeSpan.FromMinutes(2), token);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(result.Error.Trim().Split('\n').LastOrDefault() ?? $"ffmpeg exited with {result.ExitCode}");
            }

            File.Move(tmp, file, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }

        Prune();
        return file;
    }

    public static string FileUrl(string file) => new Uri(file).AbsoluteUri;

    private static string GifPath(Nade nade) => Path.Combine(CacheDir, $"{nade.Id}.gif");

    private static void Prune()
    {
        var files = new DirectoryInfo(CacheDir).GetFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
        var total = files.Sum(f => f.Length);
        foreach (var f in files)
        {
            if (total <= CacheLimit)
            {
                break;
            }

            total -= f.Length;
            f.Delete();
        }
    }
}
