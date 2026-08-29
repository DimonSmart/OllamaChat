using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.AgentRuntime;

public interface IWorkflowResultResolver
{
    Task<AgentRunResult?> ResolveAsync(
        WorkflowResultResolutionContext context,
        CancellationToken cancellationToken = default);
}

public sealed record WorkflowResultResolutionContext(
    OrchestrationWorkflowSessionStartRequest Request,
    string TaskSessionId,
    IReadOnlyList<OrchestrationCompletedAssistantMessage> Messages);

public sealed class WorkflowResultResolver(IWorkflowExecutionState executionState) : IWorkflowResultResolver
{
    public async Task<AgentRunResult?> ResolveAsync(
        WorkflowResultResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        var nonEmptyMessages = context.Messages
            .Where(static message => !string.IsNullOrWhiteSpace(message.Message.Content))
            .DistinctBy(static message => message.Message.Content.Trim())
            .ToList();
        if (nonEmptyMessages.Count == 0)
        {
            return null;
        }

        var request = context.Request;
        var finalMessage = request.Workflow switch
        {
            SequentialWorkflowDefinition sequential => ResolveSequentialWorkflowFinal(sequential, nonEmptyMessages),
            ConcurrentWorkflowDefinition concurrent => ResolveConcurrentFinal(request, concurrent, nonEmptyMessages),
            GroupChatWorkflowDefinition => await ResolveGroupChatFinalAsync(
                request, context.TaskSessionId, nonEmptyMessages, cancellationToken),
            _ => nonEmptyMessages.Last()
        };
        if (finalMessage is null)
        {
            return null;
        }

        var metadata = new Dictionary<string, string>
        {
            ["workflowKind"] = request.Workflow.Kind,
            ["finalMessageKind"] = finalMessage.SpeakerId == request.Workflow.Id
                ? "synthesized"
                : "participant"
        };
        if (!string.IsNullOrWhiteSpace(finalMessage.SpeakerId))
        {
            metadata["finalParticipantId"] = finalMessage.SpeakerId;
        }
        if (!string.IsNullOrWhiteSpace(finalMessage.Message.AgentName))
        {
            metadata["finalParticipantName"] = finalMessage.Message.AgentName;
        }

        return new AgentRunResult
        {
            FinalMessageId = finalMessage.Message.Id.ToString("N"),
            FinalMessage = new AgentOutputMessage(
                request.SessionTitle ?? request.Workflow.DisplayName,
                finalMessage.Message.Content,
                string.IsNullOrWhiteSpace(finalMessage.SpeakerId) ? null : finalMessage.SpeakerId),
            Messages = nonEmptyMessages.Select(static message => new AgentOutputMessage(
                string.IsNullOrWhiteSpace(message.Message.AgentName) ? "assistant" : message.Message.AgentName,
                message.Message.Content,
                string.IsNullOrWhiteSpace(message.SpeakerId) ? null : message.SpeakerId)).ToList(),
            Metadata = metadata
        };
    }

    private static OrchestrationCompletedAssistantMessage? ResolveSequentialWorkflowFinal(
        SequentialWorkflowDefinition workflow,
        IReadOnlyList<OrchestrationCompletedAssistantMessage> messages)
    {
        var finalParticipantId = workflow.ParticipantOrder.LastOrDefault();
        return string.IsNullOrWhiteSpace(finalParticipantId)
            ? messages.LastOrDefault()
            : messages.LastOrDefault(message => BelongsTo(message, finalParticipantId)) ?? messages.LastOrDefault();
    }

    private static OrchestrationCompletedAssistantMessage? ResolveConcurrentFinal(
        OrchestrationWorkflowSessionStartRequest request,
        ConcurrentWorkflowDefinition workflow,
        IReadOnlyList<OrchestrationCompletedAssistantMessage> messages)
    {
        var orderedMessages = OrderConcurrentMessages(workflow, messages);
        if (workflow.Aggregation.Kind == ConcurrentWorkflowAggregationKind.ConcatenateAllMessages)
        {
            return CreateSynthesizedMessage(request, string.Join(
                Environment.NewLine + Environment.NewLine,
                orderedMessages.Select(static message => message.Message.Content)));
        }

        var sections = workflow.ParticipantIds
            .Select(participantId => messages.LastOrDefault(candidate => BelongsTo(candidate, participantId)))
            .Where(static message => message is not null)
            .Select(message => $"## {(string.IsNullOrWhiteSpace(message!.Message.AgentName) ? message.SpeakerId : message.Message.AgentName)}{Environment.NewLine}{message.Message.Content}")
            .ToList();
        return sections.Count == 0
            ? null
            : CreateSynthesizedMessage(request, string.Join(Environment.NewLine + Environment.NewLine, sections));
    }

    private async Task<OrchestrationCompletedAssistantMessage?> ResolveGroupChatFinalAsync(
        OrchestrationWorkflowSessionStartRequest request,
        string taskSessionId,
        IReadOnlyList<OrchestrationCompletedAssistantMessage> messages,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Workflow.Execution.CompletionSummaryLabel))
        {
            var summary = await executionState.TryGetSummaryAsync(
                taskSessionId, request.Workflow.Execution.CompletionSummaryLabel, cancellationToken);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                return CreateSynthesizedMessage(request, summary);
            }
        }

        return messages.LastOrDefault();
    }

    private static OrchestrationCompletedAssistantMessage CreateSynthesizedMessage(
        OrchestrationWorkflowSessionStartRequest request,
        string content) =>
        new(new AppChatMessage(content, DateTime.Now, AppChatRole.Assistant,
            agentName: request.SessionTitle ?? request.Workflow.DisplayName)
        {
            Id = Guid.NewGuid()
        }, request.Workflow.Id);

    private static IReadOnlyList<OrchestrationCompletedAssistantMessage> OrderConcurrentMessages(
        ConcurrentWorkflowDefinition workflow,
        IReadOnlyList<OrchestrationCompletedAssistantMessage> messages)
    {
        var ordered = new List<OrchestrationCompletedAssistantMessage>();
        var included = new HashSet<OrchestrationCompletedAssistantMessage>();
        foreach (var participantId in workflow.ParticipantIds)
        {
            foreach (var message in messages.Where(message => BelongsTo(message, participantId)))
            {
                ordered.Add(message);
                included.Add(message);
            }
        }

        ordered.AddRange(messages.Where(message => !included.Contains(message)));
        return ordered;
    }

    private static bool BelongsTo(OrchestrationCompletedAssistantMessage message, string participantId) =>
        string.Equals(message.SpeakerId, participantId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(message.Message.AgentId, participantId, StringComparison.OrdinalIgnoreCase);
}
