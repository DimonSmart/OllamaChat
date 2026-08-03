using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.Sandbox;
using ChatClient.Api.Services.Sandbox;
using ChatClient.Domain.Models;
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
        var ragTurnId = request.RagTurnId ?? $"runtime-rag-{Guid.NewGuid():N}";
        using var ragTurn = buildResult.RagRetrievalTraceSink?.BeginTurn(ragTurnId);
        var streamedText = false;
        string? streamError = null;
        var session = await buildResult.Agent.CreateSessionAsync(cancellationToken);
        var nextInput = BuildChatMessages(request);
        var toolApprovalCoordinator = request.RuntimeResources.ToolApprovalCoordinator;

        while (true)
        {
            var projection = responseEventProjector.CreateProjection();
            ToolApprovalRequestContent? approvalRequest = null;

            await using var updates = buildResult.Agent.RunStreamingAsync(
                    nextInput,
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
                    if (responseEvent is HarnessToolApprovalRequested approval)
                    {
                        approvalRequest = update.Contents.OfType<ToolApprovalRequestContent>()
                            .FirstOrDefault(content => content.RequestId == approval.RequestId);
                        break;
                    }

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

                var traces = buildResult.RagRetrievalTraceSink?.Drain(ragTurnId) ?? [];
                if (traces.Count > 0)
                    yield return new ChatEngineStreamChunk(request.Agent.AgentName, string.Empty, RagRetrievals: traces);

                if (approvalRequest is not null || !string.IsNullOrWhiteSpace(streamError))
                {
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(streamError) || approvalRequest is null)
            {
                break;
            }

            if (toolApprovalCoordinator is null)
            {
                throw new InvalidOperationException(
                    "A session tool approval coordinator is required for shell-enabled agents.");
            }

            var toolName = GetApprovalToolName(approvalRequest);
            var scope = ToolApprovalScopeResolver.GetScope(toolName);
            var approvalDecision = await toolApprovalCoordinator.RequestApprovalAsync(
                new SessionToolApprovalRequest(
                    approvalRequest.RequestId,
                    toolName,
                    request.Agent.AgentId,
                    GetApprovalArguments(approvalRequest),
                    scope,
                    ToolApprovalScopeResolver.GetWorkspace(
                        scope,
                        request.RuntimeResources.WorkspacePath,
                        request.RuntimeResources.WorkspacePath)),
                cancellationToken);
            var approvalResponse = BuildApprovalResponse(
                approvalRequest, toolName, request.Agent.AgentId, approvalDecision,
                request.RuntimeResources.ToolApprovalPolicy);
            nextInput =
            [
                new ChatMessage(ChatRole.User, [approvalResponse])
            ];
        }

        if (!string.IsNullOrWhiteSpace(streamError))
        {
            yield return ErrorChunk(request.Agent.AgentName, streamError);
            yield break;
        }

        var finalTraces = buildResult.RagRetrievalTraceSink?.Drain(ragTurnId) ?? [];
        if (finalTraces.Count > 0)
            yield return new ChatEngineStreamChunk(request.Agent.AgentName, string.Empty, RagRetrievals: finalTraces);

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
            Temperature = AgenticRuntimeAgentFactory.ResolveTemperature(
                request.ResolvedModel,
                request.Agent.Temperature)
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

    private static AIContent BuildApprovalResponse(
        ToolApprovalRequestContent request,
        string toolName,
        string runtimeAgentId,
        ToolApprovalDecision decision,
        SessionToolApprovalPolicy? policy) =>
        ToolApprovalDecisionApplier.Apply(
            request,
            toolName,
            runtimeAgentId,
            decision,
            policy ?? throw new InvalidOperationException("A session tool approval policy is required."));

    private static readonly ToolApprovalScopeResolver ToolApprovalScopeResolver = new();

    private static string GetApprovalToolName(ToolApprovalRequestContent request) =>
        request.ToolCall switch
        {
            FunctionCallContent functionCall => functionCall.Name,
            McpServerToolCallContent mcpCall => mcpCall.Name,
            _ => "unknown"
        };

    private static string GetApprovalArguments(ToolApprovalRequestContent request) =>
        request.ToolCall switch
        {
            FunctionCallContent functionCall => SerializeArguments(functionCall.Arguments),
            McpServerToolCallContent mcpCall => SerializeArguments(mcpCall.Arguments),
            _ => "{}"
        };

    private static string SerializeArguments(object? value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
