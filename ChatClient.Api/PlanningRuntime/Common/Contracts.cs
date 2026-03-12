using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatClient.Api.PlanningRuntime.Common;

public sealed record ErrorInfo(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] JsonElement? Details = null);

public sealed record LlmFailureDetails(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("needsReplan")] bool NeedsReplan,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("details")] string[] Details);

public sealed record ResultEnvelope<T>(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("data")] T? Data,
    [property: JsonPropertyName("error")] ErrorInfo? Error)
{
    public static ResultEnvelope<T> Success(T data) => new(true, data, null);

    public static ResultEnvelope<T> Failure(string code, string message, JsonElement? details = null) =>
        new(false, default, new ErrorInfo(code, message, details));

    public T GetRequiredDataOrThrow(string operationName)
    {
        if (!Ok)
            throw new InvalidOperationException($"{operationName} failed: {Error?.Message ?? "unknown error"}");

        if (Data is null)
            throw new InvalidOperationException($"{operationName} returned no data.");

        return Data;
    }
}

