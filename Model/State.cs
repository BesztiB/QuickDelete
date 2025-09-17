using System.Text.Json.Serialization;

namespace TelegramAutoDeleteBot.Models;

public sealed class State
{
    /// <summary>Per-topic policy: key = "chatId:threadId", value = minutes</summary>
    [JsonPropertyName("TopicPolicies")]
    public Dictionary<string, TopicPolicy> TopicPolicies { get; set; } = new();

    /// <summary>Messages scheduled for deletion.</summary>
    [JsonPropertyName("Scheduled")]
    public List<ScheduledDeletion> Scheduled { get; set; } = new();

    /// <summary>Messages to delete when crossing threshold.</summary>
    [JsonPropertyName("StoredMessages")]
    public Dictionary<string, List<StoredMessage>> StoredMessages { get; set; } = new();
}

public sealed class StoredMessage
{
    [JsonPropertyName("ChatId")]
    public long ChatId { get; set; }
    [JsonPropertyName("MessageId")]
    public int MessageId { get; set; }

    [JsonPropertyName("MessageThreadId")]
    public int? MessageThreadId { get; set; }
}

public sealed class ScheduledDeletion
{
    [JsonPropertyName("ChatId")]
    public long ChatId { get; set; }

    [JsonPropertyName("MessageId")]
    public int MessageId { get; set; }

    [JsonPropertyName("MessageThreadId")]
    public int? MessageThreadId { get; set; }

    [JsonPropertyName("DeleteAtUtc")]
    public DateTime DeleteAtUtc { get; set; }
}

public sealed class TopicPolicy
{
    [JsonPropertyName("Minutes")]
    public int Minutes { get; set; } // 0 = disabled

    [JsonPropertyName("MaxMessages")]
    public int MaxMessages { get; set; } // 0 = disabled
}