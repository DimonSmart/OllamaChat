using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Runtime;
using ChatClient.Domain.Models;
#pragma warning disable MAAI001
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
#pragma warning restore MAAI001

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class OrchestrationWorkflowPassExecutor(
    ILogger<OrchestrationWorkflowPassExecutor> logger,
    OrchestrationWorkflowEventStreamProcessor eventStreamProcessor,
    IEnumerable<IOrchestrationRuntimeWorkflowBuilder> runtimeWorkflowBuilders)
{
    private readonly IReadOnlyList<IOrchestrationRuntimeWorkflowBuilder> _runtimeWorkflowBuilders =
        runtimeWorkflowBuilders.ToArray();

    internal async Task<OrchestrationWorkflowPassResult> ExecuteAsync(
        OrchestrationWorkflowPassExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workflow = BuildWorkflow(request);
        var conversation = OrchestrationWorkflowConversationBuilder.BuildConversation(request.Messages);
        var deliveredAssistantMessages =
            eventStreamProcessor.CreateDeliveredAssistantMessagesSnapshot(request.EventStreamContext);

        // Microsoft.Agents.AI.Workflows 1.0.0-rc4 drives chat-protocol runs by enqueueing the
        // initial conversation and an implicit TurnToken back-to-back inside RunAsync(). With the
        // rc4 input waiter this can overflow its binary semaphore. Drive the same protocol in
        // two explicit batches instead so each signal is consumed before the next one is sent.
        await using var run = await InProcessExecution.OpenStreamingAsync(
            workflow,
            request.SessionId,
            cancellationToken);
        var completedAssistantMessages = new List<OrchestrationCompletedAssistantMessage>();
        var streamingState = new OrchestrationWorkflowPassStreamingState(request.AssistantSpeakerIds.Count);
        var assistantOutputObserved = false;

        if (conversation.Count > 0)
        {
            assistantOutputObserved |= await ExecuteBatchAsync(
                run,
                conversation,
                request.EventStreamContext,
                streamingState,
                deliveredAssistantMessages,
                completedAssistantMessages,
                cancellationToken);
        }

        var statusAfterConversationBatch = conversation.Count == 0
            ? RunStatus.NotStarted
            : await run.GetStatusAsync(cancellationToken);

        if (ShouldSendExplicitTurnToken(
                statusAfterConversationBatch,
                completedAssistantMessages.Count))
        {
            assistantOutputObserved |= await ExecuteBatchAsync(
                run,
                new TurnToken(emitEvents: true),
                request.EventStreamContext,
                streamingState,
                deliveredAssistantMessages,
                completedAssistantMessages,
                cancellationToken);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var status = await run.GetStatusAsync(cancellationToken);
        logger.LogDebug(
            "Workflow pass completed. Status={Status}, AssistantOutputObserved={AssistantOutputObserved}, CompletedAssistantMessages={CompletedAssistantMessages}",
            status,
            assistantOutputObserved,
            completedAssistantMessages.Count);

        ValidateTerminalStatus(status, cancellationToken);

        await eventStreamProcessor.FinalizeActiveStreamsAsync(
            request.EventStreamContext,
            completedAssistantMessages);

        return new OrchestrationWorkflowPassResult(status, completedAssistantMessages);
    }

    internal static bool ShouldSendExplicitTurnToken(
        RunStatus statusAfterConversationBatch,
        int completedAssistantMessagesFromConversationBatch)
    {
        if (completedAssistantMessagesFromConversationBatch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completedAssistantMessagesFromConversationBatch));
        }

        if (statusAfterConversationBatch == RunStatus.Ended)
        {
            return false;
        }

        return completedAssistantMessagesFromConversationBatch == 0;
    }

    private async Task<bool> ExecuteBatchAsync<TInput>(
        StreamingRun run,
        TInput input,
        OrchestrationWorkflowEventStreamContext eventStreamContext,
        OrchestrationWorkflowPassStreamingState streamingState,
        IReadOnlyList<OrchestrationDeliveredAssistantMessage> deliveredAssistantMessages,
        List<OrchestrationCompletedAssistantMessage> completedAssistantMessages,
        CancellationToken cancellationToken)
        where TInput : notnull
    {
        cancellationToken.ThrowIfCancellationRequested();

        var accepted = await run.TrySendMessageAsync(input);
        if (!accepted)
        {
            throw new InvalidOperationException(
                $"Workflow rejected input of type '{typeof(TInput).Name}'.");
        }

        return await eventStreamProcessor.DrainAsync(
            run.WatchStreamAsync(cancellationToken),
            eventStreamContext,
            streamingState,
            deliveredAssistantMessages,
            completedAssistantMessages,
            cancellationToken);
    }

    private Workflow BuildWorkflow(OrchestrationWorkflowPassExecutionRequest request)
    {
        var builder = _runtimeWorkflowBuilders.FirstOrDefault(candidate => candidate.CanBuild(request.Workflow));
        if (builder is null)
        {
            throw new InvalidOperationException(
                $"Workflow kind '{request.Workflow.Kind}' does not have a registered runtime builder.");
        }

        return builder.Build(
            request.Workflow,
            request.RuntimeAgentsById,
            new OrchestrationRuntimeBuildContext
            {
                AssistantSpeakerIds = request.AssistantSpeakerIds.ToList()
            });
    }

    private static void ValidateTerminalStatus(RunStatus status, CancellationToken cancellationToken)
    {
        switch (status)
        {
            case RunStatus.Ended:
            case RunStatus.Idle:
                return;

            case RunStatus.PendingRequests:
                throw new InvalidOperationException(
                    "Workflow requested unsupported external input.");

            case RunStatus.NotStarted:
            case RunStatus.Running:
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new InvalidOperationException(
                    $"Workflow returned unexpected run status '{status}'.");

            default:
                throw new InvalidOperationException(
                    $"Workflow returned unexpected run status '{status}'.");
        }
    }
}

internal sealed class OrchestrationWorkflowPassExecutionRequest
{
    public required IOrchestrationWorkflowDefinition Workflow { get; init; }

    public required string SessionId { get; init; }

    public required IReadOnlyList<IAppChatMessage> Messages { get; init; }

    public required IReadOnlyList<string> AssistantSpeakerIds { get; init; }

    public required IReadOnlyDictionary<string, AIAgent> RuntimeAgentsById { get; init; }

    public required OrchestrationWorkflowEventStreamContext EventStreamContext { get; init; }
}
