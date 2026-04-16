using ChatClient.Api.PlanningRuntime.Runtime;
using ChatClient.Api.Services;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatClient.Api.PlanningRuntime.Shared;

public sealed class PlannerCapabilitySummary
{
    [JsonPropertyName("toolId")]
    public string ToolId { get; init; } = string.Empty;

    [JsonPropertyName("purpose")]
    public string Purpose { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("produces")]
    public string Produces { get; init; } = string.Empty;

    [JsonPropertyName("requiredInputs")]
    public IReadOnlyList<string> RequiredInputs { get; init; } = [];

    [JsonPropertyName("inputSummary")]
    public string InputSummary { get; init; } = string.Empty;

    [JsonPropertyName("outputSummary")]
    public string OutputSummary { get; init; } = string.Empty;

    [JsonPropertyName("limits")]
    public string? Limits { get; init; }

    [JsonPropertyName("constraints")]
    public string? Constraints { get; init; }

    [JsonPropertyName("compatibilityHints")]
    public IReadOnlyList<string> CompatibilityHints { get; init; } = [];
}

public static class CapabilitySummaryBuilder
{
    public static IReadOnlyList<PlannerCapabilitySummary> Build(IReadOnlyCollection<AppToolDescriptor> tools) =>
        tools
            .OrderBy(static tool => tool.QualifiedName, StringComparer.OrdinalIgnoreCase)
            .Select(BuildSummary)
            .ToList();

    private static PlannerCapabilitySummary BuildSummary(AppToolDescriptor tool)
    {
        var metadata = tool.PlanningMetadata;
        var compatibilityHints = BuildCompatibilityHints(tool, metadata);

        return new PlannerCapabilitySummary
        {
            ToolId = tool.QualifiedName,
            Purpose = metadata?.Purpose ?? tool.Description,
            Role = metadata?.PlannerRole?.ToString().ToLowerInvariant() ?? "unknown",
            Produces = metadata?.ProducesKind?.ToString().ToLowerInvariant() ?? "unknown",
            RequiredInputs = AppToolCatalog.GetRequiredInputNames(tool.InputSchema),
            InputSummary = AppToolCatalog.SummarizeSchema(tool.InputSchema),
            OutputSummary = tool.OutputSchema is { } outputSchema
                ? AppToolCatalog.SummarizeSchema(outputSchema)
                : "unknown",
            Limits = metadata?.Limits,
            Constraints = metadata?.Constraints,
            CompatibilityHints = compatibilityHints
        };
    }

    private static IReadOnlyList<string> BuildCompatibilityHints(
        AppToolDescriptor tool,
        AppToolPlanningMetadata? metadata)
    {
        var hints = new List<string>();

        if (!string.IsNullOrWhiteSpace(metadata?.Returns))
            hints.Add($"returns: {metadata.Returns.Trim()}");

        if (!string.IsNullOrWhiteSpace(metadata?.UseWhen))
            hints.Add($"use_when: {metadata.UseWhen.Trim()}");

        if (!string.IsNullOrWhiteSpace(metadata?.AvoidWhen))
            hints.Add($"avoid_when: {metadata.AvoidWhen.Trim()}");

        if (RuntimeToolCapabilityMatcher.IsBuiltInWebSearch(tool, tool.QualifiedName))
        {
            hints.Add("search.results[] is directly compatible with download.page");
            hints.Add("search.query accepts only a string");
        }

        if (RuntimeToolCapabilityMatcher.IsBuiltInWebDownload(tool, tool.QualifiedName))
        {
            hints.Add("download.page accepts a page-ref object");
            hints.Add("download.url accepts a string URL");
        }

        return hints
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
