using ChatClient.Api.Services;
using System.Text.Json.Serialization;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;

public sealed class CompactCapabilitySummary
{
    [JsonPropertyName("toolId")]
    public string ToolId { get; init; } = string.Empty;

    [JsonPropertyName("purpose")]
    public string Purpose { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("produces")]
    public string Produces { get; init; } = string.Empty;

    [JsonPropertyName("constraints")]
    public string? Constraints { get; init; }

    [JsonPropertyName("limits")]
    public string? Limits { get; init; }

    [JsonPropertyName("readOnly")]
    public bool ReadOnly { get; init; }

    [JsonPropertyName("destructive")]
    public bool Destructive { get; init; }

    [JsonPropertyName("openWorld")]
    public bool OpenWorld { get; init; }

    [JsonPropertyName("mayRequireUserInput")]
    public bool MayRequireUserInput { get; init; }
}

public static class CapabilitySummaryBuilder
{
    public static IReadOnlyList<CompactCapabilitySummary> Build(IReadOnlyCollection<AppToolDescriptor> tools) =>
        tools
            .OrderBy(static tool => tool.QualifiedName, StringComparer.OrdinalIgnoreCase)
            .Select(static tool => new CompactCapabilitySummary
            {
                ToolId = tool.QualifiedName,
                Purpose = tool.PlanningMetadata?.Purpose ?? tool.Description,
                Role = tool.PlanningMetadata?.PlannerRole?.ToString().ToLowerInvariant() ?? "unknown",
                Produces = tool.PlanningMetadata?.ProducesKind?.ToString().ToLowerInvariant() ?? "unknown",
                Constraints = tool.PlanningMetadata?.Constraints,
                Limits = tool.PlanningMetadata?.Limits,
                ReadOnly = tool.ReadOnlyHint,
                Destructive = tool.DestructiveHint,
                OpenWorld = tool.OpenWorldHint,
                MayRequireUserInput = tool.MayRequireUserInput
            })
            .ToList();
}
