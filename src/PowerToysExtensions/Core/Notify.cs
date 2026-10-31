using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace PowerToysExtensions.Core;

/// <summary>Windows toast notifications of the package (they appear as "Delido PowerToys Extensions").</summary>
public static class Notify
{
    private static ToastNotifier? _notifier;

    /// <summary>Shows a toast. With url, clicking it opens that link.</summary>
    public static void Show(string title, string body, string? url = null, string? group = null)
    {
        try
        {
            var launch = url is null ? string.Empty : $" activationType=\"protocol\" launch=\"{SecurityElement.Escape(url)}\"";
            var xml = new XmlDocument();
            xml.LoadXml(
                $"<toast{launch}><visual><binding template=\"ToastGeneric\">" +
                $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(body)}</text>" +
                "</binding></visual></toast>");
            var toast = new ToastNotification(xml);
            if (group is not null)
            {
                toast.Group = group;
            }

            _notifier ??= ToastNotificationManager.CreateToastNotifier();
            _notifier.Show(toast);
        }
        catch (Exception ex)
        {
            Log.Error("toast failed", ex);
        }
    }
}
