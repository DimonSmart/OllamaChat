using ChatClient.Api.PlanningRuntime.Common;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
        var response = await agent.RunAsync<ResultEnvelope<JsonElement?>>(
            userPrompt,
            null,
            PlanningNodeJson.SerializerOptions,
            null,
            cancellationToken);

        return response.Result
               ?? throw new InvalidOperationException($"{agentName} returned no JSON envelope.");
    }
}
