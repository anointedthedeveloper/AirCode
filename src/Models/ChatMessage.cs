namespace AirCode.Models;

public class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SenderId { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string RecipientId { get; set; } = ""; // empty = group
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsOwn { get; set; }
    public bool IsGroupMessage => string.IsNullOrEmpty(RecipientId);
    public string TimeDisplay => Timestamp.ToString("HH:mm");
}
