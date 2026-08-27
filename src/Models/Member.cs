using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AirCode.Models;

public class Member : INotifyPropertyChanged
{
    private string _displayName = "";
    private bool _isOnline = true;
    private bool _isHost;
    private string _deviceName = "";
    private DateTime _joinedAt = DateTime.Now;

    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnPropertyChanged(); OnPropertyChanged(nameof(Initials)); }
    }

    public bool IsOnline
    {
        get => _isOnline;
        set { _isOnline = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    public bool IsHost
    {
        get => _isHost;
        set { _isHost = value; OnPropertyChanged(); OnPropertyChanged(nameof(RoleText)); }
    }

    public string DeviceName
    {
        get => _deviceName;
        set { _deviceName = value; OnPropertyChanged(); }
    }

    public DateTime JoinedAt
    {
        get => _joinedAt;
        set { _joinedAt = value; OnPropertyChanged(); }
    }

    public string Initials => string.IsNullOrEmpty(DisplayName)
        ? "?"
        : DisplayName.Length >= 2
            ? $"{DisplayName[0]}{DisplayName[1]}".ToUpper()
            : DisplayName[0].ToString().ToUpper();

    public string StatusText => IsOnline ? "Online" : "Offline";
    public string RoleText => IsHost ? "Host" : "Online";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
