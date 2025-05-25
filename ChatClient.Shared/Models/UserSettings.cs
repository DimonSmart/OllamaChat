using System.Text.Json.Serialization;

namespace ChatClient.Shared.Models;

public class UserSettings
{
    /// <summary>
    /// The name of the model to use by default
    /// </summary>
    [JsonPropertyName("defaultModelName")]
    public string DefaultModelName { get; set; } = string.Empty;

    /// <summary>
    /// The default message to prepopulate in the chat input
    /// </summary>
    [JsonPropertyName("defaultChatMessage")]
    public string DefaultChatMessage { get; set; } = string.Empty;
}
