using System.Text.Json;

namespace ChatClient.Application.Services.Agentic;

/// <summary>Transport metadata for an opaque Microsoft Agent Framework Harness session.</summary>
public sealed class HarnessSessionSnapshot
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public Guid SavedAgentId { get; init; }
    public string AgentName { get; init; } = string.Empty;
    public DateTime AgentUpdatedAt { get; init; }
    public Guid ModelServerId { get; init; }
    public string ModelName { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public required AgentSessionOverrides Overrides { get; init; }
    public required JsonElement Session { get; init; }
}
