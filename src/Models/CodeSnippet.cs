namespace AirCode.Models;

public class CodeSnippet
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SenderId { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string RecipientId { get; set; } = "";
    public string Language { get; set; } = "plaintext";
    public string Code { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsOwn { get; set; }
    public string TimeDisplay => Timestamp.ToString("HH:mm");
}
