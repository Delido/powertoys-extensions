using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerToysExtensions.Core;

public static class Paths
{
    /// <summary>LocalState of the package (or %LOCALAPPDATA%\Delido.PowerToysExtensions when unpackaged).</summary>
    public static string Data { get; } = Create(Utilities.BaseSettingsPath("Delido.PowerToysExtensions"));

    /// <summary>Folder of the running executable, where Assets and Helpers live.</summary>
    public static string App { get; } = AppContext.BaseDirectory;

    public static string For(string extension) => Create(Path.Combine(Data, extension));

    public static string Asset(string relative) => Path.Combine(App, "Assets", relative);

    private static string Create(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
