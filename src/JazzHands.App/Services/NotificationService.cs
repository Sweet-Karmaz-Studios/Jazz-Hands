using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JazzHands.App.Services;

/// <summary>How much a notification matters.</summary>
public enum NotificationLevel
{
    /// <summary>Something finished or happened.</summary>
    Information,

    /// <summary>Something fell back or is missing, and the work goes on.</summary>
    Warning,

    /// <summary>Something failed.</summary>
    Error,
}

/// <summary>One notification.</summary>
/// <param name="Level">How much it matters.</param>
/// <param name="Text">What it says.</param>
/// <param name="At">When.</param>
public sealed record Notification(NotificationLevel Level, string Text, DateTimeOffset At)
{
    /// <summary>The time, as the history shows it.</summary>
    public string Time => At.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Toasts in the corner of the window, and a history of every one.</summary>
public interface INotificationService
{
    /// <summary>Shows a notification for a few seconds and keeps it in the history.</summary>
    void Show(NotificationLevel level, string text);
}

/// <summary>
/// The shell's notifications: up to three toasts at once in the bottom right, each for five
/// seconds, and every one kept (the last hundred) for the bell in the status bar.
/// </summary>
/// <remarks>
/// A notification can be raised on any thread; it reaches the lists on the UI thread. The same
/// text again while its toast still shows does not stack a second one.
/// </remarks>
public sealed partial class NotificationService : ObservableObject, INotificationService
{
    /// <summary>How many toasts show at once.</summary>
    public const int MaxToasts = 3;

    /// <summary>How many the history keeps.</summary>
    public const int MaxHistory = 100;

    private readonly IUiDispatcher _ui;
    private readonly TimeSpan _toastFor;

    [ObservableProperty]
    private int _unread;

    /// <summary>Notifications over the UI thread.</summary>
    /// <param name="ui">The UI thread.</param>
    /// <param name="toastFor">How long a toast shows: five seconds.</param>
    public NotificationService(IUiDispatcher ui, TimeSpan? toastFor = null)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _toastFor = toastFor ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>What shows in the corner now, oldest first.</summary>
    public ObservableCollection<Notification> Toasts { get; } = [];

    /// <summary>Every notification, newest first.</summary>
    public ObservableCollection<Notification> History { get; } = [];

    /// <inheritdoc />
    public void Show(NotificationLevel level, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var notification = new Notification(level, text, DateTimeOffset.Now);
        _ui.Post(() =>
        {
            History.Insert(0, notification);
            while (History.Count > MaxHistory)
            {
                History.RemoveAt(History.Count - 1);
            }

            Unread++;
            if (Toasts.Any(toast => toast.Text == text))
            {
                return;
            }

            Toasts.Add(notification);
            while (Toasts.Count > MaxToasts)
            {
                Toasts.RemoveAt(0);
            }

            _ = Task.Delay(_toastFor).ContinueWith(_ => _ui.Post(() => Toasts.Remove(notification)), TaskScheduler.Default);
        });
    }

    /// <summary>Marks the history read: the bell was opened.</summary>
    public void MarkRead() => Unread = 0;

    /// <summary>Dismisses a toast before its time.</summary>
    public void Dismiss(Notification notification) => Toasts.Remove(notification);
}
