using System.Text.Json.Serialization;

namespace TelegramAutoDeleteBot.Models;

public sealed class Settings
{
    [JsonPropertyName("BotToken")]
    public string BotToken { get; set; } = "";

    /// <summary>Only these group/supergroup chat IDs are processed.</summary>
    [JsonPropertyName("AllowedChatIds")]
    public List<long> AllowedChatIds { get; set; } = new();
}
