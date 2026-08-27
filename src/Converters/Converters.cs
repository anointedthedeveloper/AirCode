using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AirCode.Models;
using AirCode.ViewModels;

namespace AirCode.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object v, Type t, object p, CultureInfo c)
        => (v is bool b && (Invert ? !b : b)) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => v is Visibility vis && vis == Visibility.Visible;
}

public class ConnectionStateToColorConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is ConnectionState cs ? cs switch
        {
            ConnectionState.ConnectedAsHost or ConnectionState.ConnectedAsClient
                => new SolidColorBrush(Color.FromRgb(34, 197, 94)),   // green
            ConnectionState.Connecting
                => new SolidColorBrush(Color.FromRgb(251, 191, 36)),  // amber
            _ => new SolidColorBrush(Color.FromRgb(148, 163, 184))    // slate
        } : Binding.DoNothing;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class TransferStatusToColorConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is TransferStatus ts ? ts switch
        {
            TransferStatus.Completed => new SolidColorBrush(Color.FromRgb(34, 197, 94)),
            TransferStatus.Failed or TransferStatus.Declined or TransferStatus.Cancelled
                => new SolidColorBrush(Color.FromRgb(239, 68, 68)),
            TransferStatus.Active => new SolidColorBrush(Color.FromRgb(37, 99, 235)),
            _ => new SolidColorBrush(Color.FromRgb(148, 163, 184))
        } : Binding.DoNothing;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class PageEqualityConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is AppPage page && p is string pStr && Enum.TryParse<AppPage>(pStr, out var target) && page == target;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class TransferDirectionConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is TransferDirection d ? (d == TransferDirection.Sending ? "→" : "←") : "";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v != null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class IsConnectingConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is ConnectionState s && s == ConnectionState.Connecting
            ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is bool b ? !b : false;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => v is bool b ? !b : false;
}

public class StringToInitialsConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        if (v is not string s || string.IsNullOrEmpty(s)) return "?";
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) return $"{parts[0][0]}{parts[1][0]}".ToUpper();
        return s.Length >= 2 ? s[..2].ToUpper() : s.ToUpper();
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
