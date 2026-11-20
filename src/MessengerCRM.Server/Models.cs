namespace MessengerCRM.Server;

public record LoginRequest(string Username, string Password);
public record SetupRequest(string SetupCode, string Username, string Password);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record TelegramConfigRequest(int ApiId, string? ApiHash, string Phone);
public record TelegramPublicConfig(int ApiId, string Phone, bool HasApiHash);
public record TelegramLoginRequest(string Value);
public record SendMessageRequest(string Name, string Phone, string Channel, string Text);
public record BulkSendRequest(int[] ContactIds, string Channel, string Text);
public record ContactCheckRequest(int[] ContactIds);
public record MessageDeleteRequest(string[] MessageIds);
public record ExportRequest(int[]? ContactIds);
public record TelegramQrStartRequest(string? Password);
public record TelegramPasswordRequest(string Password);
public record PhoneLookupRequest(string Phone);
public record WhatsAppIncomingRequest(string Jid, string? Phone, string? MessageId, string Text, long? Timestamp);
public record WhatsAppReadRequest(string Jid, string MessageId);
public record PresenceResult(string State, string? Detail = null);
public record MessageObservation(bool Found, bool Read, string? ReplyText = null);
public record ContactCreateRequest(string Name, string Phone, string? Notes);
public record ImportResult(int Added, int Duplicates, int Invalid);

public sealed class ContactRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool IsArchived { get; set; }
    public string TelegramState { get; set; } = "unknown";
    public string WhatsAppState { get; set; } = "unknown";
    public string? TelegramDeliveryState { get; set; }
    public string? WhatsAppDeliveryState { get; set; }
    public long? TelegramUserId { get; set; }
    public string? WhatsAppJid { get; set; }
    public DateTime? ChannelsCheckedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class MessageRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ContactName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Direction { get; set; } = "out";
    public string Text { get; set; } = "";
    public string Status { get; set; } = "queued";
    public long? ExternalPeerId { get; set; }
    public long? ExternalMessageId { get; set; }
    public string? ExternalPeerKey { get; set; }
    public string? ExternalMessageKey { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
    public DateTime? RepliedAtUtc { get; set; }
    public string? ReplyText { get; set; }
    public string? Error { get; set; }
}

public record ChannelState(string Channel, bool Configured, bool Connected, string Status, string? Account = null);
public record QueueSummary(int Queued, int Sent, int Read, int Replied, int Failed, int Incoming);
public record ExportResultRow(string Name, string Phone, string Result);
