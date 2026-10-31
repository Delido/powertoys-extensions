namespace PowerToysExtensions.Core;

/// <summary>Small file log in the data folder (log.txt, rotated at 1 MB). Never logs secrets.</summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static readonly string File = Path.Combine(Paths.Data, "log.txt");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(File);
                if (info.Exists && info.Length > 1024 * 1024)
                {
                    System.IO.File.Move(File, File + ".1", overwrite: true);
                }

                System.IO.File.AppendAllText(File, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // logging must never break a command
        }
    }
}
