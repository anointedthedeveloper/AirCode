namespace AirCode.Models;

public class AppSettings
{
    public string DisplayName { get; set; } = Environment.MachineName;
    public string DownloadFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AirCode", "Downloads");
    public bool DarkMode { get; set; } = false;
    public bool NotificationsEnabled { get; set; } = true;
    public bool FirstRun { get; set; } = true;
    public string LastNetworkName { get; set; } = "AirCode-Classroom";
}
