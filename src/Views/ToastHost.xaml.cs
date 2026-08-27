using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AirCode.Services;

namespace AirCode.Views;

public class ToastItem
{
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public SolidColorBrush AccentColor { get; set; } = new(Color.FromRgb(37, 99, 235));
}

public partial class ToastHost : UserControl
{
    private readonly ObservableCollection<ToastItem> _toasts = new();

    public ToastHost()
    {
        InitializeComponent();
        ToastList.ItemsSource = _toasts;
    }

    public void Show(string title, string message, NotificationKind kind)
    {
        var color = kind switch
        {
            NotificationKind.Success => Color.FromRgb(34, 197, 94),
            NotificationKind.Warning => Color.FromRgb(251, 191, 36),
            NotificationKind.Error   => Color.FromRgb(239, 68, 68),
            _                        => Color.FromRgb(37, 99, 235)
        };

        var toast = new ToastItem
        {
            Title = title,
            Message = message,
            AccentColor = new SolidColorBrush(color)
        };

        _toasts.Add(toast);

        // Auto-dismiss after 3.5s
        var timer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromSeconds(3.5) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            _toasts.Remove(toast);
        };
        timer.Start();
    }
}
