using ChatClient.Domain.Models;
using System.Text.Json;

namespace ChatClient.Api.Client.Services.Agentic;

internal sealed record ProviderMessage(
    string Role,
    string? Content,
    string? Name,
    string? ToolCallId,
    IReadOnlyList<ProviderToolCall>? ToolCalls);

internal sealed record ProviderToolCall(string Id, string Name, string Arguments);

internal sealed record ProviderAssistantResponse(
    string Content,
    IReadOnlyList<ProviderToolCall> ToolCalls,
    string? Error = null)
{
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public static ProviderAssistantResponse FromError(string error) => new(string.Empty, [], error);
}

internal sealed record ToolBinding(
    string ServerName,
    string ToolName,
    string ProviderName,
    string Description,
    JsonElement JsonSchema,
    Func<Dictionary<string, object?>, CancellationToken, Task<object>> ExecuteAsync,
    bool MayRequireUserInput = false);

internal sealed record ToolRegistry(
    IReadOnlyList<ToolBinding> Tools,
    IReadOnlyDictionary<string, ToolBinding> ToolsByProviderName)
{
    public static ToolRegistry Empty { get; } =
        new([], new Dictionary<string, ToolBinding>(StringComparer.OrdinalIgnoreCase));

    public bool HasTools => Tools.Count > 0;
}

internal sealed record ToolExecutionResult(ProviderMessage ToolMessage, FunctionCallRecord Record);
