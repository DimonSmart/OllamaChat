using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
#pragma warning disable MAAI001
using Microsoft.Agents.AI.Workflows;
#pragma warning restore MAAI001

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class OrchestrationWorkflowTurnCoordinator(
    ILogger<OrchestrationWorkflowTurnCoordinator> logger,
    IWorkflowExecutionPolicy executionPolicy)
{
    public async Task RunAsync(
        OrchestrationWorkflowTurnExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var progress = new WorkflowExecutionProgress();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var completionReached = await request.IsExecutionCompleteAsync(cancellationToken);
            var beforePassDecision = executionPolicy.EvaluateBeforePass(
                request.Execution,
                progress,
                completionReached);
            if (await ApplyDecisionAsync(request, beforePassDecision))
            {
                return;
            }

            progress = progress with { StartedPasses = progress.StartedPasses + 1 };

            var passResult = await request.ExecutePassAsync(cancellationToken);
            if (passResult.CompletedAssistantMessages.Count > 0)
            {
                await request.ProcessCompletedAssistantMessagesAsync(
                    passResult.CompletedAssistantMessages,
                    cancellationToken);
            }

            progress = progress with
            {
                AutomaticAssistantTurnsUsed = progress.AutomaticAssistantTurnsUsed +
                                              passResult.CompletedAssistantMessages.Count
            };

            completionReached = await request.IsExecutionCompleteAsync(cancellationToken);
            var afterPassDecision = executionPolicy.EvaluateAfterPass(
                request.Execution,
                progress,
                new WorkflowPassObservation(
                    passResult.Status == RunStatus.Ended,
                    passResult.CompletedAssistantMessages.Count),
                completionReached);
            if (await ApplyDecisionAsync(request, afterPassDecision))
            {
                return;
            }
        }
    }

    private async Task<bool> ApplyDecisionAsync(
        OrchestrationWorkflowTurnExecutionRequest request,
        WorkflowExecutionDecision decision)
    {
        if (decision.Warning != WorkflowExecutionWarning.None)
        {
            LogWarning(request, decision.Warning);
        }

        switch (decision.Action)
        {
            case WorkflowExecutionAction.Continue:
                return false;

            case WorkflowExecutionAction.Stop:
                return true;

            case WorkflowExecutionAction.ShowAssistantError:
                await request.HandleAssistantErrorAsync(
                    decision.ErrorMessage ?? "Workflow returned an empty response.");
                return true;

            case WorkflowExecutionAction.ThrowExceededAutomaticTurns:
                throw new InvalidOperationException(
                    $"Autonomous workflow '{request.WorkflowDisplayName}' exceeded its automatic turn limit ({request.Execution.MaxAutomaticTurns}).");

            default:
                throw new InvalidOperationException(
                    $"Unsupported workflow execution action '{decision.Action}'.");
        }
    }

    private void LogWarning(
        OrchestrationWorkflowTurnExecutionRequest request,
        WorkflowExecutionWarning warning)
    {
        switch (warning)
        {
            case WorkflowExecutionWarning.EndedWithoutCompletionMarkers:
                logger.LogWarning(
                    "Autonomous workflow '{WorkflowDisplayName}' ended without reaching completion markers. CompletionPhase={CompletionPhase}, CompletionSummaryLabel={CompletionSummaryLabel}",
                    request.WorkflowDisplayName,
                    request.Execution.CompletionPhase,
                    request.Execution.CompletionSummaryLabel);
                break;

            case WorkflowExecutionWarning.StoppedWithoutCompletionMarkers:
                logger.LogWarning(
                    "Autonomous workflow '{WorkflowDisplayName}' stopped after reaching its automatic turn limit without reaching completion markers. CompletionPhase={CompletionPhase}, CompletionSummaryLabel={CompletionSummaryLabel}",
                    request.WorkflowDisplayName,
                    request.Execution.CompletionPhase,
                    request.Execution.CompletionSummaryLabel);
                break;
        }
    }
}

public sealed class OrchestrationWorkflowTurnExecutionRequest
{
    public required string WorkflowDisplayName { get; init; }

    public required AgentWorkflowExecutionDefinition Execution { get; init; }

    public required Func<CancellationToken, Task<bool>> IsExecutionCompleteAsync { get; init; }

    public required Func<CancellationToken, Task<OrchestrationWorkflowPassResult>> ExecutePassAsync { get; init; }

    public required Func<IReadOnlyList<OrchestrationCompletedAssistantMessage>, CancellationToken, Task> ProcessCompletedAssistantMessagesAsync { get; init; }

    public required Func<string, Task> HandleAssistantErrorAsync { get; init; }
}

public sealed record OrchestrationCompletedAssistantMessage(
    AppChatMessage Message,
    string? SpeakerId);

public sealed record OrchestrationWorkflowPassResult(
    RunStatus Status,
    IReadOnlyList<OrchestrationCompletedAssistantMessage> CompletedAssistantMessages);
