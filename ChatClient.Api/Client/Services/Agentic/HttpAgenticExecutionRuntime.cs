using ChatClient.Domain.Models;
using ChatClient.Application.Services.Agentic;
#pragma warning disable MAAI001
using Microsoft.Agents.AI;
#pragma warning restore MAAI001
using Microsoft.Extensions.AI;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class HttpAgenticExecutionRuntime(
    AgenticRuntimeAgentFactory runtimeAgentFactory,
    HarnessResponseEventProjector responseEventProjector,
    ILogger<HttpAgenticExecutionRuntime> logger) : IAgenticExecutionRuntime
{
    public async IAsyncEnumerable<ChatEngineStreamChunk> StreamAsync(
        AgentRunRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        HarnessAgentRuntimeDefinition? buildResult = null;
        string? startupError = null;

        try
        {
            buildResult = await runtimeAgentFactory.CreateAsync(
                request,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to prepare agent runtime for agent {AgentName} using model {ModelName}",
                request.Agent.AgentName,
                request.ResolvedModel.ModelName);
            startupError = ex.Message;
        }

        if (!string.IsNullOrWhiteSpace(startupError) || buildResult is null)
        {
            yield return ErrorChunk(request.Agent.AgentName, startupError ?? "Failed to prepare the agent runtime.");
            yield break;
        }

        var runOptions = BuildRunOptions(request, buildResult.Server, buildResult.ToolSet);
        var streamedText = false;
        var projection = responseEventProjector.CreateProjection();
        string? streamError = null;

        var session = await buildResult.Agent.CreateSessionAsync(cancellationToken);
        await using var updates = buildResult.Agent.RunStreamingAsync(
                BuildChatMessages(request),
                session,
                runOptions,
                cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            AgentResponseUpdate update;
            try
            {
                if (!await updates.MoveNextAsync())
                {
                    break;
                }

                update = updates.Current;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Agentic session failed for agent {AgentName}", request.Agent.AgentName);
                streamError = ex.Message;
                break;
            }

            foreach (var responseEvent in projection.Project(update, buildResult.ToolSet.MetadataByName))
            {
                if (responseEvent is HarnessTextDelta textDelta)
                {
                    if (!string.IsNullOrWhiteSpace(textDelta.Text))
                    {
                        streamedText = true;
                    }

                    yield return new ChatEngineStreamChunk(
                        request.Agent.AgentName,
                        textDelta.Text,
                        Event: responseEvent);
                    continue;
                }

                yield return new ChatEngineStreamChunk(
                    request.Agent.AgentName,
                    string.Empty,
                    Event: responseEvent);
            }
        }

        if (!string.IsNullOrWhiteSpace(streamError))
        {
            yield return ErrorChunk(request.Agent.AgentName, streamError);
            yield break;
        }

        if (!streamedText)
        {
            yield return new ChatEngineStreamChunk(
                request.Agent.AgentName,
                "Model returned an empty response.",
                IsFinal: true,
                IsError: true);
            yield break;
        }

        yield return new ChatEngineStreamChunk(
            request.Agent.AgentName,
            string.Empty,
            IsFinal: true);
    }

    private static ChatClientAgentRunOptions BuildRunOptions(
        AgentRunRequest request,
        LlmServerConfig server,
        AgenticToolSet toolSet)
    {
        var chatOptions = new ChatOptions
        {
            ModelId = request.ResolvedModel.ModelName,
            Temperature = request.Agent.Temperature is double temperature
                ? (float)temperature
                : null
        };

        if (toolSet.HasTools)
        {
            chatOptions.AllowMultipleToolCalls = true;
            chatOptions.ToolMode = ChatToolMode.Auto;
        }

        if (server.ServerType == ServerType.Ollama && request.Agent.RepeatPenalty.HasValue)
        {
            chatOptions.AdditionalProperties ??= [];
            chatOptions.AdditionalProperties["repeat_penalty"] = request.Agent.RepeatPenalty.Value;
        }

        return new ChatClientAgentRunOptions(chatOptions);
    }

    private static List<ChatMessage> BuildChatMessages(AgentRunRequest request)
    {
        List<ChatMessage> result = [];

        foreach (var message in request.Conversation)
        {
            var text = message.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            result.Add(new ChatMessage(message.Role.ToAiChatRole(), text));
        }

        if (!result.Any(static message => message.Role == ChatRole.User) &&
            !string.IsNullOrWhiteSpace(request.UserMessage))
        {
            result.Add(new ChatMessage(ChatRole.User, request.UserMessage.Trim()));
        }

        return result;
    }

    private static ChatEngineStreamChunk ErrorChunk(string agentName, string message) =>
        new(agentName, message, IsFinal: true, IsError: true);
}
