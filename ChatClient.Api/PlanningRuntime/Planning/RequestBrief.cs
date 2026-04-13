using System.Text.Json.Serialization;

namespace ChatClient.Api.PlanningRuntime.Planning;

/// <summary>
/// Structured output of the request clarification step.
/// Replaces the previous <c>PlanningRequestAnalysis</c> with a richer typed contract
/// that captures not only acquisition and reasoning needs but also the expected result,
/// success criteria, ambiguity, and output expectations.
/// </summary>
public sealed class RequestBrief
{
    /// <summary>The user request rewritten into a clearer, normalised form.</summary>
    [JsonPropertyName("rewrittenRequest")]
    public string RewrittenRequest { get; init; } = string.Empty;

    /// <summary>The main goal that the plan must achieve.</summary>
    [JsonPropertyName("goal")]
    public string Goal { get; init; } = string.Empty;

    /// <summary>
    /// Description of the expected result artifact: what kind of thing it is
    /// (e.g. "ranked list", "comparison table", "factual summary", "side-by-side analysis").
    /// </summary>
    [JsonPropertyName("expectedResult")]
    public string ExpectedResult { get; init; } = string.Empty;

    /// <summary>What the final answer or external outcome must contain.</summary>
    [JsonPropertyName("deliverables")]
    public List<string> Deliverables { get; init; } = [];

    /// <summary>Explicit and strongly-implied constraints on the solution.</summary>
    [JsonPropertyName("constraints")]
    public List<string> Constraints { get; init; } = [];

    /// <summary>
    /// External information or external actions that must be obtained or performed
    /// to satisfy the request (acquisition-level needs).
    /// </summary>
    [JsonPropertyName("acquisitionNeeds")]
    public List<string> AcquisitionNeeds { get; init; } = [];

    /// <summary>
    /// Evidence that must be present and traceable in the final result
    /// (e.g. "exact prices from product pages", "cited publication dates").
    /// </summary>
    [JsonPropertyName("evidenceRequirements")]
    public List<string> EvidenceRequirements { get; init; } = [];

    /// <summary>
    /// Transformations, comparisons, normalization, ranking, synthesis, or validation
    /// work that the plan must perform after data is available.
    /// </summary>
    [JsonPropertyName("reasoningNeeds")]
    public List<string> ReasoningNeeds { get; init; } = [];

    /// <summary>
    /// Conditions that must be true for the result to be considered successful
    /// (e.g. "at least 5 distinct items", "all prices verified against source").
    /// </summary>
    [JsonPropertyName("successCriteria")]
    public List<string> SuccessCriteria { get; init; } = [];

    /// <summary>
    /// Open questions or ambiguous parts of the request that the planner should
    /// be aware of (e.g. "unclear whether 'best' means by rating or by price").
    /// </summary>
    [JsonPropertyName("ambiguityNotes")]
    public List<string> AmbiguityNotes { get; init; } = [];

    /// <summary>
    /// Output format and presentation expectations
    /// (e.g. "markdown table", "bullet list in user language", "JSON for downstream use").
    /// </summary>
    [JsonPropertyName("outputExpectations")]
    public string OutputExpectations { get; init; } = string.Empty;

    /// <summary>Coarse-grained logical steps of the solution (not concrete tool calls).</summary>
    [JsonPropertyName("suggestedPlanOutline")]
    public List<string> SuggestedPlanOutline { get; init; } = [];

    public void ValidateOrThrow()
    {
        if (string.IsNullOrWhiteSpace(RewrittenRequest))
            throw new InvalidOperationException("RequestBrief must include rewrittenRequest.");

        if (string.IsNullOrWhiteSpace(Goal))
            throw new InvalidOperationException("RequestBrief must include goal.");

        if (SuggestedPlanOutline.Count == 0)
            throw new InvalidOperationException("RequestBrief must include at least one suggestedPlanOutline item.");

        if (HasBlank(Deliverables))
            throw new InvalidOperationException("RequestBrief deliverables must not contain blank items.");

        if (HasBlank(Constraints))
            throw new InvalidOperationException("RequestBrief constraints must not contain blank items.");

        if (HasBlank(AcquisitionNeeds))
            throw new InvalidOperationException("RequestBrief acquisitionNeeds must not contain blank items.");

        if (HasBlank(EvidenceRequirements))
            throw new InvalidOperationException("RequestBrief evidenceRequirements must not contain blank items.");

        if (HasBlank(ReasoningNeeds))
            throw new InvalidOperationException("RequestBrief reasoningNeeds must not contain blank items.");

        if (HasBlank(SuccessCriteria))
            throw new InvalidOperationException("RequestBrief successCriteria must not contain blank items.");

        if (HasBlank(AmbiguityNotes))
            throw new InvalidOperationException("RequestBrief ambiguityNotes must not contain blank items.");

        if (HasBlank(SuggestedPlanOutline))
            throw new InvalidOperationException("RequestBrief suggestedPlanOutline must not contain blank items.");
    }

    private static bool HasBlank(IEnumerable<string> items) =>
        items.Any(string.IsNullOrWhiteSpace);
}
