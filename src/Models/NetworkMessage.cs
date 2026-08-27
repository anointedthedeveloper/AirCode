namespace AirCode.Models;

/// <summary>All wire-protocol message types exchanged over WebSocket.</summary>
public enum MessageType
{
    // Handshake
    Register, RegisterAck, MemberList, MemberJoined, MemberLeft, MemberUpdated,
    // Chat
    ChatMessage, DirectMessage,
    // Code
    CodeShare,
    // File transfer
    FileOffer, FileAccept, FileDecline, FileChunk, FileComplete, FileError,
    // Control
    Ping, Pong, Disconnect, NameChange
}

public class NetworkMessage
{
    public MessageType Type { get; set; }
    public string SenderId { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string RecipientId { get; set; } = ""; // empty = broadcast
    public string Payload { get; set; } = "";
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
