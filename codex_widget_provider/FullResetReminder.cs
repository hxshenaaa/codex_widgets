using Microsoft.Windows.AppNotifications.Builder;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;

namespace CodexUsageWidgetProvider;

/// <summary>Maintains one Windows reminder for the expiry shown on the Full reset card.</summary>
internal static class FullResetReminder
{
    private const string Group = "CodexUsageWidget";
    private const string Tag = "FullResetExpiry";
    private const string ScheduledExpiryKey = "FullResetReminderExpiry";
    private static readonly TimeSpan LeadTime = TimeSpan.FromHours(24);
    private static readonly object Gate = new();

    public static void Sync(WidgetDisplayData data)
    {
        lock (Gate)
        {
            var notifier = ToastNotificationManager.CreateToastNotifier();
            var pending = notifier.GetScheduledToastNotifications()
                .Where(item => item.Group == Group && item.Tag == Tag)
                .ToList();

            long? expirySeconds = data.hasFullResets ? data.fullResetExpiresAt : null;
            DateTimeOffset now = DateTimeOffset.Now;
            DateTimeOffset? expiry = expirySeconds is > 0
                ? DateTimeOffset.FromUnixTimeSeconds(expirySeconds.Value)
                : null;
            DateTimeOffset? reminderAt = expiry - LeadTime;

            if (expiry is not null && reminderAt > now &&
                pending.Count == 1 && pending[0].DeliveryTime == reminderAt.Value)
            {
                // The host can restart after scheduling; keep the marker in sync.
                Remember(expirySeconds!.Value);
                return;
            }

            foreach (var item in pending)
            {
                notifier.RemoveFromSchedule(item);
            }

            if (expiry is null || expiry <= now)
            {
                Forget();
                return;
            }

            // Once scheduled, this marker prevents another toast when the 24-hour
            // point passes and the scheduled item leaves the Windows queue.
            if (AlreadyScheduled(expirySeconds!.Value))
            {
                return;
            }

            var notification = CreateNotification(expiry.Value);
            if (reminderAt > now)
            {
                notifier.AddToSchedule(new ScheduledToastNotification(notification, reminderAt.Value)
                {
                    Group = Group,
                    Tag = Tag,
                });
            }
            else
            {
                // A credit discovered inside its final 24 hours still gets one reminder.
                notifier.Show(new ToastNotification(notification) { Group = Group, Tag = Tag });
            }
            Remember(expirySeconds.Value);
        }
    }

    public static void Cancel()
    {
        lock (Gate)
        {
            var notifier = ToastNotificationManager.CreateToastNotifier();
            foreach (var item in notifier.GetScheduledToastNotifications()
                .Where(item => item.Group == Group && item.Tag == Tag))
            {
                notifier.RemoveFromSchedule(item);
            }
            Forget();
        }
    }

    private static XmlDocument CreateNotification(DateTimeOffset expiry)
    {
        string expiryText = expiry.LocalDateTime.ToString("yyyy年MM月dd日 HH:mm");
        var payload = new AppNotificationBuilder()
            .AddText("Codex Full reset 即将到期")
            .AddText($"你的 Full reset 将于 {expiryText} 到期。")
            .BuildNotification()
            .Payload;
        var document = new XmlDocument();
        document.LoadXml(payload);
        return document;
    }

    private static bool AlreadyScheduled(long expirySeconds) =>
        ApplicationData.Current.LocalSettings.Values[ScheduledExpiryKey] is long saved &&
        saved == expirySeconds;

    private static void Remember(long expirySeconds) =>
        ApplicationData.Current.LocalSettings.Values[ScheduledExpiryKey] = expirySeconds;

    private static void Forget() =>
        ApplicationData.Current.LocalSettings.Values.Remove(ScheduledExpiryKey);
}
