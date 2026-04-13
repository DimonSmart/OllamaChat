using System.Text.Json.Serialization;

namespace ChatClient.Api.PlanningRuntime.Planning;

/// <summary>
/// Describes what the final result of a workflow plan must look like.
/// Attached to <see cref="PlanDefinition"/> during the planning phase and used by the
/// validator and repair loop to verify semantic completeness.
/// </summary>
public sealed class ResultContract
{
    /// <summary>
    /// What kind of artifact the result is
    /// (e.g. "ranked list", "comparison table", "factual summary", "action confirmation").
    /// </summary>
    [JsonPropertyName("expectedArtifactType")]
    public string ExpectedArtifactType { get; init; } = string.Empty;

    /// <summary>Whether the result must be directly presented to the user.</summary>
    [JsonPropertyName("userVisible")]
    public bool UserVisible { get; init; } = true;

    /// <summary>
    /// Evidence policy: what evidence must be traceable in the result
    /// (e.g. "all values must come from tool output", "sources must be cited").
    /// Null means no specific evidence requirement is enforced.
    /// </summary>
    [JsonPropertyName("evidenceRequirement")]
    public string? EvidenceRequirement { get; init; }

    /// <summary>
    /// Language policy for the result
    /// (e.g. "same language as user request", "English only").
    /// Null means no specific language is enforced.
    /// </summary>
    [JsonPropertyName("languagePolicy")]
    public string? LanguagePolicy { get; init; }

    /// <summary>
    /// Completeness requirements: conditions the result must satisfy
    /// (e.g. "at least 5 distinct items", "all requested fields must be present").
    /// </summary>
    [JsonPropertyName("completenessRequirements")]
    public List<string> CompletenessRequirements { get; init; } = [];

    /// <summary>
    /// Formatting requirements for the result
    /// (e.g. "markdown table", "bullet list", "plain prose").
    /// Null means no specific format is enforced.
    /// </summary>
    [JsonPropertyName("formattingRequirements")]
    public string? FormattingRequirements { get; init; }
}

/// <summary>
/// Derives a <see cref="ResultContract"/> from a <see cref="RequestBrief"/>.
/// Called by the planner after clarification to establish the result contract
/// before materialization begins.
/// </summary>
internal static class ResultContractDeriver
{
    public static ResultContract DeriveFrom(RequestBrief brief)
    {
        var evidenceRequirement = brief.EvidenceRequirements.Count > 0
            ? string.Join("; ", brief.EvidenceRequirements)
            : null;

        var formattingRequirements = string.IsNullOrWhiteSpace(brief.OutputExpectations)
            ? null
            : brief.OutputExpectations;

        var completeness = brief.SuccessCriteria.Count > 0
            ? brief.SuccessCriteria
            : brief.Deliverables;

        return new ResultContract
        {
            ExpectedArtifactType = string.IsNullOrWhiteSpace(brief.ExpectedResult)
                ? InferArtifactType(brief)
                : brief.ExpectedResult,
            UserVisible = true,
            EvidenceRequirement = evidenceRequirement,
            LanguagePolicy = null,
            CompletenessRequirements = [.. completeness],
            FormattingRequirements = formattingRequirements
        };
    }

    private static string InferArtifactType(RequestBrief brief)
    {
        var goal = brief.Goal.ToLowerInvariant();

        if (goal.Contains("compar") || goal.Contains("сравн"))
            return "comparison";

        if (goal.Contains("rank") || goal.Contains("list") || goal.Contains("top ") ||
            goal.Contains("рейтинг") || goal.Contains("список"))
            return "ranked list";

        if (goal.Contains("summar") || goal.Contains("overview") || goal.Contains("резюм") ||
            goal.Contains("обзор"))
            return "summary";

        return "result";
    }
}
