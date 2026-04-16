using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatClient.Api.PlanningRuntime.Shared;

public sealed class PlanningIssue
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("layer")]
    public string Layer { get; init; } = string.Empty;

    [JsonPropertyName("details")]
    public JsonElement? Details { get; init; }

    [JsonPropertyName("isBlocking")]
    public bool IsBlocking { get; init; } = true;
}
