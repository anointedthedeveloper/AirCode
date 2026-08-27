using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AirCode.Models;

public enum TransferDirection { Sending, Receiving }
public enum TransferStatus { Pending, Active, Completed, Failed, Declined, Cancelled }

public class FileTransfer : INotifyPropertyChanged
{
    private double _progress;
    private TransferStatus _status = TransferStatus.Pending;
    private string _speedText = "";
    private long _bytesTransferred;

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public string PeerId { get; set; } = "";
    public string PeerName { get; set; } = "";
    public TransferDirection Direction { get; set; }
    public string? SavePath { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }

    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressText)); }
    }

    public long BytesTransferred
    {
        get => _bytesTransferred;
        set { _bytesTransferred = value; OnPropertyChanged(); }
    }

    public TransferStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(IsActive)); }
    }

    public string SpeedText
    {
        get => _speedText;
        set { _speedText = value; OnPropertyChanged(); }
    }

    public string FileSizeText => FormatSize(FileSize);
    public string ProgressText => $"{FormatSize(BytesTransferred)} / {FormatSize(FileSize)}";
    public string StatusText => Status.ToString();
    public bool IsActive => Status == TransferStatus.Active || Status == TransferStatus.Pending;

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
