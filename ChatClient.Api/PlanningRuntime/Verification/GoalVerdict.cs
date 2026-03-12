using System.Text.Json.Serialization;

namespace ChatClient.Api.PlanningRuntime.Verification;

public enum GoalAction
{
    Done,
    Replan,
    AskUser
}

public sealed class GoalVerdict
{
    [JsonPropertyName("action")]
    public GoalAction Action { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;

    [JsonPropertyName("missing")]
    public List<string> Missing { get; init; } = new();

    [JsonPropertyName("userQuestion")]
    public string? UserQuestion { get; init; }
}

