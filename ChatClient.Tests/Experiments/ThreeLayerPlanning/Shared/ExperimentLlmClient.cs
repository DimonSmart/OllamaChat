using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Shared;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;

public interface IExperimentLlmClient : IPlanningLlmClient
{
}

public sealed class ChatClientExperimentLlmClient(IChatClient chatClient) : IExperimentLlmClient
{
    public async Task<PlanningJsonGenerationResult<T>> GenerateJsonAsync<T>(
        string agentName,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        var agent = new ChatClientAgent(chatClient, systemPrompt, agentName, null, null, null, null);
        var response = await agent.RunAsync<T>(
            userPrompt,
            null,
            ExperimentJson.SerializerOptions,
            null,
            cancellationToken);

        var result = response.Result
                     ?? throw new InvalidOperationException($"{agentName} returned no typed JSON result.");
        var rawResponse = response.Text?.Trim() ?? string.Empty;
        JsonNode? rawJson = null;
        if (!string.IsNullOrWhiteSpace(rawResponse))
        {
            try
            {
                rawJson = JsonNode.Parse(rawResponse);
            }
            catch
            {
                rawJson = null;
            }
        }

        return new PlanningJsonGenerationResult<T>
        {
            Result = result,
            RawResponse = rawResponse,
            RawJson = rawJson
        };
    }

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
            ExperimentJson.SerializerOptions,
            null,
            cancellationToken);

        return response.Result
               ?? throw new InvalidOperationException($"{agentName} returned no JSON envelope.");
    }
}
