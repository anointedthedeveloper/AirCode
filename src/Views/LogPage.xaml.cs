using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using AirCode.Services;

namespace AirCode.Views;

public partial class LogPage : UserControl
{
    private readonly LogService _log = LogService.Instance;
    private ObservableCollection<LogEntry>? _filtered;
    private string _activeFilter = "All";

    public LogPage() => InitializeComponent();

    public void Initialize()
    {
        ApplyFilter("All");
        // Auto-scroll when new entries arrive
        _log.EntryAdded += _ => Dispatcher.InvokeAsync(() =>
        {
            ApplyFilter(_activeFilter, scroll: true);
        });
        SetActiveFilter(FilterAll);
    }

    private void ApplyFilter(string filter, bool scroll = false)
    {
        _activeFilter = filter;
        IEnumerable<LogEntry> source = _log.Entries;

        if (filter != "All")
        {
            if (Enum.TryParse<LogLevel>(filter, out var lvl))
                source = source.Where(e => e.Level == lvl);
        }

        _filtered = new ObservableCollection<LogEntry>(source);
        LogList.ItemsSource = _filtered;

        if (scroll)
            LogScroll.ScrollToTop(); // newest is at top (inserted at 0)
    }

    private void Filter_Click(object s, RoutedEventArgs e)
    {
        if (s is Button btn)
        {
            ApplyFilter(btn.Tag?.ToString() ?? "All");
            SetActiveFilter(btn);
        }
    }

    private void SetActiveFilter(Button active)
    {
        foreach (var b in new[] { FilterAll, FilterInfo, FilterWarning, FilterError, FilterSuccess })
        {
            b.Style = b == active
                ? (Style)FindResource("PrimaryButton")
                : (Style)FindResource("SecondaryButton");
        }
    }

    private void CopyAll_Click(object s, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_log.CopyAll());
            NotificationService.Instance.Show("Logs copied", "All log entries copied to clipboard.",
                NotificationKind.Success);
        }
        catch { }
    }

    private void CopyLine_Click(object s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is LogEntry entry)
        {
            try { Clipboard.SetText(entry.Display); } catch { }
        }
    }

    private void Clear_Click(object s, RoutedEventArgs e)
    {
        _log.Clear();
        ApplyFilter(_activeFilter);
    }
}
