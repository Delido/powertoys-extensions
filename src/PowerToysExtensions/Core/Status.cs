using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace PowerToysExtensions.Core;

/// <summary>
/// Runs an async operation behind a Command Palette status message (Raycast's animated toast):
/// "working …" while it runs, then success or the error.
/// </summary>
public static class Status
{
    public static async Task<bool> Run(string working, string done, Func<Task> action)
    {
        var message = new StatusMessage { Message = working, State = MessageState.Info, Progress = new ProgressState { IsIndeterminate = true } };
        ExtensionHost.ShowStatus(message, StatusContext.Extension);
        try
        {
            await action();
            ExtensionHost.HideStatus(message);
            new ToastStatusMessage(new StatusMessage { Message = done, State = MessageState.Success }).Show();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(working, ex);
            ExtensionHost.HideStatus(message);
            new ToastStatusMessage(new StatusMessage { Message = $"{working.TrimEnd('…', ' ', '.')} failed: {ex.Message}", State = MessageState.Error }) { Duration = 6000 }.Show();
            return false;
        }
    }

    /// <summary>Fire-and-forget variant for command handlers.</summary>
    public static void Start(string working, string done, Func<Task> action, Action? after = null) =>
        _ = Task.Run(async () =>
        {
            if (await Run(working, done, action))
            {
                after?.Invoke();
            }
        });

    public static void Error(string message) =>
        new ToastStatusMessage(new StatusMessage { Message = message, State = MessageState.Error }) { Duration = 6000 }.Show();

    public static void Info(string message) => new ToastStatusMessage(message).Show();
}
