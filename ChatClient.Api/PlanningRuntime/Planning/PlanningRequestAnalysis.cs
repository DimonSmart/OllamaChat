using System.Text.Json.Serialization;

namespace ChatClient.Api.PlanningRuntime.Planning;

public sealed class PlanningRequestAnalysis
{
    [JsonPropertyName("rewrittenRequest")]
    public string RewrittenRequest { get; init; } = string.Empty;

    [JsonPropertyName("goal")]
    public string Goal { get; init; } = string.Empty;

    [JsonPropertyName("deliverables")]
    public List<string> Deliverables { get; init; } = [];

    [JsonPropertyName("constraints")]
    public List<string> Constraints { get; init; } = [];

    [JsonPropertyName("acquisitionNeeds")]
    public List<string> AcquisitionNeeds { get; init; } = [];

    [JsonPropertyName("reasoningNeeds")]
    public List<string> ReasoningNeeds { get; init; } = [];

    [JsonPropertyName("suggestedPlanOutline")]
    public List<string> SuggestedPlanOutline { get; init; } = [];

    public void ValidateOrThrow()
    {
        if (string.IsNullOrWhiteSpace(RewrittenRequest))
            throw new InvalidOperationException("Request analysis must include rewrittenRequest.");

        if (string.IsNullOrWhiteSpace(Goal))
            throw new InvalidOperationException("Request analysis must include goal.");

        if (SuggestedPlanOutline.Count == 0)
            throw new InvalidOperationException("Request analysis must include at least one suggestedPlanOutline item.");

        if (HasBlank(Deliverables))
            throw new InvalidOperationException("Request analysis deliverables must not contain blank items.");

        if (HasBlank(Constraints))
            throw new InvalidOperationException("Request analysis constraints must not contain blank items.");

        if (HasBlank(AcquisitionNeeds))
            throw new InvalidOperationException("Request analysis acquisitionNeeds must not contain blank items.");

        if (HasBlank(ReasoningNeeds))
            throw new InvalidOperationException("Request analysis reasoningNeeds must not contain blank items.");

        if (HasBlank(SuggestedPlanOutline))
            throw new InvalidOperationException("Request analysis suggestedPlanOutline must not contain blank items.");
    }

    private static bool HasBlank(IEnumerable<string> items) =>
        items.Any(string.IsNullOrWhiteSpace);
}
