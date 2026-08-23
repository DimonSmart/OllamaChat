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
    [JsonIgnore] public string? StorageRoot { get; set; }
}

public sealed class SavedChatLaunchSnapshot
{
    [JsonPropertyName("runtimeReference")] public SavedChatRuntimeReference? RuntimeReference { get; set; }
    [JsonPropertyName("model")] public ServerModel? Model { get; set; }
    [JsonPropertyName("inputs")] public Dictionary<string, string> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("overrides")] public SavedChatOverrides Overrides { get; set; } = new();
}

public sealed class SavedChatOverrides
{
    [JsonPropertyName("mcpServerBindings")] public List<McpServerSessionBinding>? McpServerBindings { get; set; }
    [JsonPropertyName("workspacePath")] public string? WorkspacePath { get; set; }
    [JsonPropertyName("sandboxProfileId")] public Guid? SandboxProfileId { get; set; }
}

public sealed record SavedChatRuntimeReference([property: JsonPropertyName("kind")] string Kind, [property: JsonPropertyName("id")] string Id);

public sealed class SavedChatNativeSession
{
    [JsonPropertyName("snapshot")] public string SnapshotJson { get; set; } = string.Empty;
}

public sealed record SavedChatSummary(Guid Id, string Title, DateTime UpdatedAtUtc, DateTime CreatedAtUtc, SavedChatRuntimeReference? RuntimeReference);
