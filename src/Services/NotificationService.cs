using System.Windows;

namespace AirCode.Services;

/// <summary>In-app toast-style notifications (bottom-right overlay).</summary>
public class NotificationService
{
    private static NotificationService? _instance;
    public static NotificationService Instance => _instance ??= new NotificationService();

    public bool IsEnabled { get; set; } = true;

    public event Action<string, string, NotificationKind>? NotificationRequested;

    public void Show(string title, string message, NotificationKind kind = NotificationKind.Info)
    {
        if (!IsEnabled) return;
        Application.Current?.Dispatcher.InvokeAsync(() =>
            NotificationRequested?.Invoke(title, message, kind));
    }
}

public enum NotificationKind { Info, Success, Warning, Error }
