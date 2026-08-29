using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Application.Helpers;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using System.Text;

namespace ChatClient.Api.Services.AgentRuntime;

public sealed class WorkflowAgentRuntimeFactory(
    IWorkflowDefinitionService workflowDefinitionService,
    IWorkflowDefinitionCompiler workflowDefinitionCompiler,
    IWorkflowParticipantResolver workflowParticipantResolver,
    IWorkflowParticipantRuntimeFactory participantRuntimeFactory,
    IWorkflowExecutionEngine workflowExecutionEngine,
    ILogger<WorkflowAgentRuntimeFactory> logger) : IWorkflowAgentRuntimeFactory
{
    public async Task<IAgentRuntime> CreateAsync(
        string workflowId,
        AgentRuntimeCreationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Guid.TryParse(workflowId, out var savedWorkflowId))
        {
            throw new KeyNotFoundException($"Workflow id '{workflowId}' is not a valid saved-workflow id.");
        }

        var savedWorkflow = await workflowDefinitionService.GetByIdAsync(savedWorkflowId);
        if (savedWorkflow is null)
        {
            throw new KeyNotFoundException($"Saved workflow '{workflowId}' was not found.");
        }

        var compiled = await workflowDefinitionCompiler.CompileAsync(
            savedWorkflow.SourceCode,
            cancellationToken);
        var compiledWorkflow = compiled.Workflow
            ?? throw new InvalidOperationException("Workflow compilation did not return a workflow definition.");
        var resolvedParticipants = await workflowParticipantResolver.ResolveAsync(
            compiledWorkflow,
            cancellationToken);
        var runtimeParticipants = new List<WorkflowRuntimeParticipant>();
        foreach (var participant in resolvedParticipants)
        {
            runtimeParticipants.Add(await participantRuntimeFactory.CreateAsync(
                participant,
                context,
                cancellationToken));
        }

        return new WorkflowAgentRuntime(
            new AgentRuntimeDescriptor(
                savedWorkflow.Id.ToString("D"),
                savedWorkflow.DisplayName,
                savedWorkflow.Description,
                AgentRuntimeKind.WorkflowAgent),
            compiledWorkflow,
            runtimeParticipants,
            context.Configuration,
            context,
            workflowExecutionEngine,
            logger);
    }

}

internal sealed class WorkflowAgentRuntime(
    AgentRuntimeDescriptor descriptor,
    IOrchestrationWorkflowDefinition workflow,
    IReadOnlyList<WorkflowRuntimeParticipant> runtimeParticipants,
    AppChatConfiguration configuration,
    AgentRuntimeCreationContext creationContext,
    IWorkflowExecutionEngine workflowExecutionEngine,
    ILogger logger) : IAgentRuntime
{
    public AgentRuntimeDescriptor Descriptor { get; } = descriptor;

    public async IAsyncEnumerable<AgentRunEvent> RunAsync(
        AgentRuntimeRunRequest request,
        AgentRunContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var currentUserMessageIndex = FindCurrentUserMessageIndex(request.Messages);
        var userMessage = currentUserMessageIndex >= 0
            ? request.Messages[currentUserMessageIndex]
            : null;
        var runsWithoutUserMessage =
            workflow.Execution.Mode == AgentWorkflowExecutionMode.Autonomous &&
            request.InvocationKind is AgentRuntimeInvocationKind.RunOnStart or
                AgentRuntimeInvocationKind.WorkflowParticipant;
        if (!runsWithoutUserMessage && (userMessage is null || string.IsNullOrWhiteSpace(userMessage.Content)))
        {
            yield return new AgentRunFailed(new AgentRunError(
                "invalid_input",
                "At least one non-empty user message is required.",
                false));
            yield break;
        }

        if (currentUserMessageIndex >= 0 && request.Messages.Skip(currentUserMessageIndex + 1).Any())
        {
            yield return new AgentRunFailed(new AgentRunError(
                "invalid_input",
                "Messages after the current user message are not supported.",
                false));
            yield break;
        }

        if (!TryBuildStartInputsWithAttachments(
                request,
                out var startInputs,
                out var attachmentError))
        {
            yield return new AgentRunFailed(new AgentRunError(
                "invalid_input",
                attachmentError ?? "Workflow attachments could not be mapped to start inputs.",
                false));
            yield break;
        }

        var workflowRequest = new WorkflowExecutionRequest
        {
            Workflow = workflow,
            Participants = runtimeParticipants,
            Configuration = configuration,
            StartInputs = startInputs,
            SessionTitle = Descriptor.Name,
            SessionDescription = Descriptor.Description,
            ParentRunContext = context,
            CreationContext = creationContext,
        };

        var events = workflowExecutionEngine.ExecuteAsync(workflowRequest with
        {
            UserMessage = userMessage is null
                ? null
                : BuildWorkflowUserMessage(
                    request.Messages.Take(currentUserMessageIndex),
                    userMessage.Content)
        }, cancellationToken);

        await using var enumerator = events.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            AgentRunEvent? runEvent = null;
            AgentRunFailed? failure = null;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                runEvent = enumerator.Current;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = MapWorkflowException(context, ex);
            }

            if (failure is not null)
            {
                yield return failure;
                yield break;
            }

            if (runEvent is not null)
            {
                yield return runEvent;
            }
        }
    }

    private AgentRunFailed MapWorkflowException(
        AgentRunContext context,
        Exception ex)
    {
        if (TryExtractAgentRunError(ex) is { } runError)
        {
            return new AgentRunFailed(runError);
        }

        if (ex is WorkflowProducedNoResultException)
        {
            return new AgentRunFailed(new AgentRunError(
                "workflow_produced_no_result",
                "Workflow produced no final assistant result.",
                false,
                ex));
        }

        logger.LogError(
            ex,
            "Workflow agent runtime failed. RunId={RunId}, WorkflowId={WorkflowId}, WorkflowName={WorkflowName}, WorkflowKind={WorkflowKind}, ParticipantCount={ParticipantCount}",
            context.RunId,
            Descriptor.Id,
            Descriptor.Name,
            workflow.Kind,
            workflow.Participants.Count);

        return new AgentRunFailed(new AgentRunError(
            "workflow_execution_failed",
            "Workflow execution failed.",
            true,
            ex));
    }

    private static AgentRunError? TryExtractAgentRunError(Exception exception)
    {
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<Exception>();
        queue.Enqueue(exception);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            if (current is AgentRunFailedException runFailure)
            {
                return runFailure.Error;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    queue.Enqueue(inner);
                }
            }

            if (current.InnerException is not null)
            {
                queue.Enqueue(current.InnerException);
            }
        }

        return null;
    }

    private static int FindCurrentUserMessageIndex(IReadOnlyList<AgentInputMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == AgentMessageRole.User)
            {
                return index;
            }
        }

        return -1;
    }

    private static string BuildWorkflowUserMessage(
        IEnumerable<AgentInputMessage> previousMessages,
        string currentMessage)
    {
        var history = previousMessages
            .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
            .Where(static message => message.Role is AgentMessageRole.System or AgentMessageRole.User or AgentMessageRole.Assistant)
            .ToList();

        if (history.Count == 0)
        {
            return currentMessage;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Previous conversation:");
        builder.AppendLine();
        foreach (var message in history)
        {
            var role = message.Role switch
            {
                AgentMessageRole.System => "System",
                AgentMessageRole.Assistant => "Assistant",
                _ => "User"
            };
            builder.AppendLine($"{role}: {message.Content}");
            builder.AppendLine();
        }

        builder.AppendLine("Current request:");
        builder.AppendLine();
        builder.Append(currentMessage);
        return builder.ToString();
    }

    private bool TryBuildStartInputsWithAttachments(
        AgentRuntimeRunRequest request,
        out IReadOnlyList<OrchestrationWorkflowStartInputValue> startInputs,
        out string? error)
    {
        var values = request.Inputs
            .Select(static input => new OrchestrationWorkflowStartInputValue
            {
                Key = input.Key,
                Value = input.Value
            })
            .ToList();

        error = null;
        if (request.Attachments.Count == 0)
        {
            startInputs = values;
            return true;
        }

        var markdownInputs = workflow.StartInputs
            .Where(static input => input.Kind == WorkflowStartInputKind.MarkdownDocument)
            .ToList();
        var requiredMarkdownInputs = markdownInputs
            .Where(static input => input.IsRequired)
            .ToList();

        if (request.Attachments.Count != 1 || requiredMarkdownInputs.Count != 1)
        {
            startInputs = [];
            error = "Workflow attachments require exactly one attachment and exactly one required MarkdownDocument start input.";
            return false;
        }

        var attachment = request.Attachments[0];
        if (!IsMarkdownOrTextAttachment(attachment))
        {
            startInputs = [];
            error = $"Attachment '{attachment.Name}' is not a supported markdown/text attachment.";
            return false;
        }

        if (values.Any(value => string.Equals(value.Key, requiredMarkdownInputs[0].Key, StringComparison.OrdinalIgnoreCase)))
        {
            startInputs = [];
            error = $"Workflow start input '{requiredMarkdownInputs[0].Key}' was provided both as an input and as an attachment.";
            return false;
        }

        values.Add(new OrchestrationWorkflowStartInputValue
        {
            Key = requiredMarkdownInputs[0].Key,
            Value = string.IsNullOrWhiteSpace(attachment.Content)
                ? Encoding.UTF8.GetString(attachment.Data)
                : attachment.Content
        });
        startInputs = values;
        return true;
    }

    private static bool IsMarkdownOrTextAttachment(AgentInputAttachment attachment)
    {
        if (attachment.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return attachment.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
               attachment.Name.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase) ||
               attachment.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
    }

}
