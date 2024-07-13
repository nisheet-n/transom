using System.Diagnostics;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Transom.Services;

public static class Notifications
{
    public static event Action<string, string>? ReminderAction;

    public static void Register()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, args) =>
            {
                args.Arguments.TryGetValue("action", out var action);
                args.Arguments.TryGetValue("id", out var id);
                if (id is not null) App.DispatcherQueue?.TryEnqueue(() => ReminderAction?.Invoke(action ?? "open", id));
            };
            AppNotificationManager.Default.Register();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public static void Unregister()
    {
        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public static void ShowReminder(Reminder reminder)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddArgument("id", reminder.Id)
                .SetScenario(AppNotificationScenario.Reminder)
                .AddText("Reminder")
                .AddText(reminder.Text)
                .AddButton(new AppNotificationButton("Snooze 5 min").AddArgument("action", "snooze").AddArgument("id", reminder.Id))
                .AddButton(new AppNotificationButton("Done").AddArgument("action", "done").AddArgument("id", reminder.Id))
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }
}
