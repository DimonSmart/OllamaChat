using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.ClientModel;
using System.Text.Json;

namespace ChatClient.Api.PlanningRuntime.Shared;

public interface IPlanningLlmClient
{
    Task<ResultEnvelope<JsonElement?>> GenerateEnvelopeAsync(
        string agentName,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default);
}

public sealed class ChatClientPlanningLlmClient(IChatClient chatClient) : IPlanningLlmClient
{
    public async Task<ResultEnvelope<JsonElement?>> GenerateEnvelopeAsync(
        string agentName,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        var agent = new ChatClientAgent(chatClient, systemPrompt, agentName, null, null, null, null);

        try
        {
            var response = await agent.RunAsync<ResultEnvelope<JsonElement?>>(
                userPrompt,
                null,
                PlanningNodeJson.SerializerOptions,
                null,
                cancellationToken);

            return response.Result
                   ?? throw new InvalidOperationException($"{agentName} returned no JSON envelope.");
        }
        catch (ClientResultException ex) when (RuntimeLlmPromptScope.Current is not null && IsContextLengthExceeded(ex))
        {
            return RuntimeLlmPromptOverflow.CreateFailure(RuntimeLlmPromptScope.Current);
        }
    }

    private static bool IsContextLengthExceeded(ClientResultException exception)
    {
        var message = exception.Message ?? string.Empty;
        var looksLikeHttp400 = exception.Status == 400
            || (exception.Status == 0 && message.Contains("HTTP 400", StringComparison.OrdinalIgnoreCase));
        if (!looksLikeHttp400)
            return false;

        return message.Contains("invalid_request_error", StringComparison.OrdinalIgnoreCase)
               && message.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase)
               && message.Contains("Parameter: messages", StringComparison.OrdinalIgnoreCase);
    }
}
