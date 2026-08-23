using System.Text.Json.Serialization;

namespace ChatClient.Domain.Models;

public sealed class SavedChatDocument
{
    public const int CurrentFormatVersion = 1;
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = CurrentFormatVersion;
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("isTitleManual")] public bool IsTitleManual { get; set; }
    [JsonPropertyName("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [JsonPropertyName("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
    [JsonPropertyName("launch")] public SavedChatLaunchSnapshot Launch { get; set; } = new();
    [JsonPropertyName("messages")] public List<AppChatMessage> Messages { get; set; } = [];
    [JsonPropertyName("nativeSession")] public SavedChatNativeSession? NativeSession { get; set; }
}

public sealed class SavedChatLaunchSnapshot
{
    public SavedChatRuntimeReference? RuntimeReference { get; set; }
    public ServerModel? Model { get; set; }
    public Dictionary<string, string> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public SavedChatOverrides Overrides { get; set; } = new();
}

public sealed class SavedChatOverrides
{
    public List<McpServerSessionBinding>? McpServerBindings { get; set; }
    public string? WorkspacePath { get; set; }
    public Guid? SandboxProfileId { get; set; }
}

public sealed record SavedChatRuntimeReference(string Kind, string Id);

public sealed class SavedChatNativeSession
{
    public string SessionJson { get; set; } = string.Empty;
}

public sealed record SavedChatSummary(Guid Id, string Title, DateTime UpdatedAtUtc, DateTime CreatedAtUtc, SavedChatRuntimeReference? RuntimeReference);
