using System.Collections.ObjectModel;
using System.Text;

namespace AirCode.Services;

public enum LogLevel { Info, Success, Warning, Error, Debug }

public class LogEntry
{
    public DateTime Time     { get; init; } = DateTime.Now;
    public LogLevel Level    { get; init; }
    public string   Category { get; init; } = "";
    public string   Message  { get; init; } = "";

    public string TimeText  => Time.ToString("HH:mm:ss");
    public string LevelText => Level.ToString().ToUpper();
    public string Display   => $"[{TimeText}] [{LevelText}] [{Category}] {Message}";

    // Color hint for UI
    public string LevelColor => Level switch
    {
        LogLevel.Success => "#22C55E",
        LogLevel.Warning => "#F59E0B",
        LogLevel.Error   => "#EF4444",
        LogLevel.Debug   => "#64748B",
        _                => "#94A3B8"
    };
}

/// <summary>
/// App-wide logger. Thread-safe. Exposes an ObservableCollection for the Log page
/// and a PlainText property for copy-to-clipboard.
/// </summary>
public class LogService
{
    private static LogService? _instance;
    public  static LogService  Instance => _instance ??= new LogService();

    private readonly object _lock = new();

    public ObservableCollection<LogEntry> Entries { get; } = new();
    public int MaxEntries { get; set; } = 1000;

    public event Action<LogEntry>? EntryAdded;

    public void Log(LogLevel level, string category, string message)
    {
        var entry = new LogEntry { Level = level, Category = category, Message = message };

        lock (_lock)
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                Entries.Insert(0, entry);
                while (Entries.Count > MaxEntries)
                    Entries.RemoveAt(Entries.Count - 1);
            });
        }

        EntryAdded?.Invoke(entry);
        System.Diagnostics.Debug.WriteLine(entry.Display);
    }

    public void Info   (string cat, string msg) => Log(LogLevel.Info,    cat, msg);
    public void Success(string cat, string msg) => Log(LogLevel.Success,  cat, msg);
    public void Warn   (string cat, string msg) => Log(LogLevel.Warning,  cat, msg);
    public void Error  (string cat, string msg) => Log(LogLevel.Error,    cat, msg);
    public void Debug  (string cat, string msg) => Log(LogLevel.Debug,    cat, msg);

    public string CopyAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"AirCode Log Export — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('─', 60));
        lock (_lock)
        {
            foreach (var e in Entries.Reverse())
                sb.AppendLine(e.Display);
        }
        return sb.ToString();
    }

    public void Clear()
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(Entries.Clear);
    }
}
