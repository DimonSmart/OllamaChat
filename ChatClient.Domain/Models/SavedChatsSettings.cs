using System.Text.Json.Serialization;

namespace ChatClient.Domain.Models;

public sealed class SavedChatsSettings
{
    [JsonPropertyName("autoSaveEnabled")]
    public bool AutoSaveEnabled { get; set; } = true;

    [JsonPropertyName("storageRoot")]
    public string StorageRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OllamaChat", "Chats");

    [JsonPropertyName("titleGeneration")]
    public ChatTitleGenerationSettings TitleGeneration { get; set; } = new();
}

public sealed class ChatTitleGenerationSettings
{
    [JsonPropertyName("serverId")]
    public Guid? ServerId { get; set; }

    [JsonPropertyName("modelName")]
    public string? ModelName { get; set; }

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;
}
