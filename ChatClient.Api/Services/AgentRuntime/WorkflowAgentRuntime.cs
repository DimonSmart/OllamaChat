using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Compatibility;
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
    ILegacyWorkflowDefinitionNormalizer legacyWorkflowDefinitionNormalizer,
    IWorkflowParticipantResolver workflowParticipantResolver,
    IWorkflowParticipantRuntimeFactory participantRuntimeFactory,
    IHeadlessWorkflowRunner headlessWorkflowRunner,
    IWorkflowParticipantInvoker participantInvoker,
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
        var workflow = await legacyWorkflowDefinitionNormalizer.NormalizeAsync(
            compiledWorkflow,
            cancellationToken);
        var resolvedParticipants = await workflowParticipantResolver.ResolveAsync(
            workflow,
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
            workflow,
            resolvedParticipants,
            runtimeParticipants,
            context.Configuration,
            context,
            headlessWorkflowRunner,
            participantInvoker,
            logger);
    }

}

internal sealed class WorkflowAgentRuntime(
    AgentRuntimeDescriptor descriptor,
    IOrchestrationWorkflowDefinition workflow,
    IReadOnlyList<ResolvedWorkflowParticipant> participants,
    IReadOnlyList<WorkflowRuntimeParticipant> runtimeParticipants,
    AppChatConfiguration configuration,
    AgentRuntimeCreationContext creationContext,
    IHeadlessWorkflowRunner headlessWorkflowRunner,
    IWorkflowParticipantInvoker participantInvoker,
    ILogger logger) : IAgentRuntime
{
    public AgentRuntimeDescriptor Descriptor { get; } = descriptor;

    public async IAsyncEnumerable<AgentRunEvent> RunAsync(
        AgentRuntimeRunRequest request,
        AgentRunContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (workflow is SequentialWorkflowDefinition sequentialWorkflow)
        {
            await foreach (var runEvent in RunSequentialAsync(
                               sequentialWorkflow,
                               request,
                               context,
                               cancellationToken))
            {
                yield return runEvent;
            }

            yield break;
        }

        var currentUserMessageIndex = FindCurrentUserMessageIndex(request.Messages);
        var userMessage = currentUserMessageIndex >= 0
            ? request.Messages[currentUserMessageIndex]
            : null;
        if (userMessage is null || string.IsNullOrWhiteSpace(userMessage.Content))
        {
            yield return new AgentRunFailed(new AgentRunError(
                "invalid_input",
                "At least one non-empty user message is required.",
                false));
            yield break;
        }

        if (request.Messages.Skip(currentUserMessageIndex + 1).Any())
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

        var workflowRequest = new HeadlessWorkflowSessionStartRequest
        {
            Workflow = workflow,
            Participants = runtimeParticipants,
            Configuration = configuration,
            StartInputs = startInputs,
            SessionTitle = Descriptor.Name,
            SessionDescription = Descriptor.Description,
            ParentRunContext = context,
            CreationContext = creationContext,
            ResolvedParticipants = participants,
            ParticipantInvoker = participantInvoker
        };

        IHeadlessWorkflowSession? session = null;
        AgentRunFailed? startFailure = null;
        try
        {
            session = await headlessWorkflowRunner.StartAsync(workflowRequest, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            startFailure = MapWorkflowException(context, ex);
        }

        if (startFailure is not null)
        {
            yield return startFailure;
            yield break;
        }

        var activeSession = session!;
        await using (activeSession)
        {
            var events = activeSession.RunTurnAsync(new HeadlessWorkflowTurnRequest
            {
                UserMessage = BuildWorkflowUserMessage(
                    request.Messages.Take(currentUserMessageIndex),
                    userMessage.Content)
            }, cancellationToken);

            await using var enumerator = events.GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                HeadlessWorkflowEvent? headlessEvent = null;
                AgentRunFailed? failure = null;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }

                    headlessEvent = enumerator.Current;
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

                if (headlessEvent is null)
                {
                    yield break;
                }

                if (headlessEvent is HeadlessWorkflowStarted)
                {
                    continue;
                }

                yield return MapHeadlessEvent(headlessEvent);
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

    private static AgentRunEvent MapHeadlessEvent(HeadlessWorkflowEvent headlessEvent) =>
        headlessEvent switch
        {
            HeadlessWorkflowTextDelta delta => new AgentTextDelta(
                delta.MessageId,
                delta.Author,
                delta.Text),
            HeadlessWorkflowMessageCompleted completed => new AgentMessageCompleted(
                completed.MessageId,
                new AgentOutputMessage(completed.Author, completed.Content)),
            HeadlessWorkflowCompleted completed => new AgentRunCompleted(new AgentRunResult
            {
                FinalMessage = new AgentOutputMessage(
                    completed.Result.FinalAuthor,
                    completed.Result.FinalContent),
                FinalMessageId = completed.Result.FinalMessageId,
                Messages = completed.Result.Messages
                    .Select(static message => new AgentOutputMessage(message.Author, message.Content))
                    .ToList(),
                Metadata = completed.Result.Metadata
            }),
            _ => throw new InvalidOperationException(
                $"Unsupported headless workflow event '{headlessEvent.GetType().Name}'.")
        };

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

    private async IAsyncEnumerable<AgentRunEvent> RunSequentialAsync(
        SequentialWorkflowDefinition sequentialWorkflow,
        AgentRuntimeRunRequest request,
        AgentRunContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!TryBuildStartInputsWithAttachments(request, out _, out var attachmentError))
        {
            yield return new AgentRunFailed(new AgentRunError(
                "invalid_input",
                attachmentError ?? "Workflow attachments could not be mapped to start inputs.",
                false));
            yield break;
        }

        var participantsById = participants.ToDictionary(
            static participant => participant.ParticipantId,
            StringComparer.OrdinalIgnoreCase);
        if (sequentialWorkflow.ParticipantOrder.Count == 0)
        {
            yield return new AgentRunFailed(new AgentRunError(
                "invalid_workflow",
                "Sequential workflow has no participant order.",
                false));
            yield break;
        }

        AgentRunResult? previousResult = null;
        var outputMessages = new List<AgentOutputMessage>();
        var messageId = Guid.NewGuid().ToString("N");

        foreach (var participantId in sequentialWorkflow.ParticipantOrder)
        {
            if (!participantsById.TryGetValue(participantId, out var participant))
            {
                yield return new AgentRunFailed(new AgentRunError(
                    "invalid_workflow",
                    $"Sequential workflow participant '{participantId}' was not resolved.",
                    false));
                yield break;
            }

            var participantRequest = SequentialParticipantRequestBuilder.Build(
                request,
                participant,
                previousResult);
            AgentRunResult? terminalResult = null;
            AgentRunFailed? terminalFailure = null;

            await foreach (var participantEvent in participantInvoker.InvokeAsync(
                               participant,
                               participantRequest,
                               creationContext,
                               context,
                               cancellationToken))
            {
                switch (participantEvent)
                {
                    case AgentTextDelta delta when IsFinalParticipant(sequentialWorkflow, participant.ParticipantId):
                        yield return new AgentTextDelta(messageId, Descriptor.Name, delta.Text);
                        break;
                    case AgentRunCompleted completed:
                        terminalResult = completed.Result;
                        break;
                    case AgentRunFailed failed:
                        terminalFailure = failed;
                        break;
                }
            }

            if (terminalFailure is not null)
            {
                yield return terminalFailure;
                yield break;
            }

            if (terminalResult is null)
            {
                yield return new AgentRunFailed(new AgentRunError(
                    "participant_protocol_violation",
                    $"Workflow participant '{participant.ParticipantId}' completed without a terminal result.",
                    false));
                yield break;
            }

            previousResult = terminalResult;
            outputMessages.Add(terminalResult.FinalMessage);
        }

        if (previousResult is null)
        {
            yield return new AgentRunFailed(new AgentRunError(
                "workflow_produced_no_result",
                "Workflow produced no final assistant result.",
                false));
            yield break;
        }

        var finalMessage = new AgentOutputMessage(
            Descriptor.Name,
            previousResult.FinalMessage.Content);
        var metadata = BuildSequentialMetadata(
            sequentialWorkflow,
            outputMessages,
            participantsById,
            previousResult);
        yield return new AgentMessageCompleted(messageId, finalMessage);
        yield return new AgentRunCompleted(new AgentRunResult
        {
            FinalMessage = finalMessage,
            FinalMessageId = messageId,
            Messages = outputMessages.Concat([finalMessage]).ToList(),
            Metadata = metadata
        });
    }

    private IReadOnlyDictionary<string, string> BuildSequentialMetadata(
        SequentialWorkflowDefinition sequentialWorkflow,
        IReadOnlyList<AgentOutputMessage> outputMessages,
        IReadOnlyDictionary<string, ResolvedWorkflowParticipant> participantsById,
        AgentRunResult finalParticipantResult)
    {
        var metadata = new Dictionary<string, string>
        {
            ["runtime.kind"] = "workflow",
            ["workflow.id"] = Descriptor.Id,
            ["workflow.name"] = Descriptor.Name,
            ["workflow.kind"] = workflow.Kind,
            ["workflow.participant.count"] = sequentialWorkflow.ParticipantOrder.Count.ToString()
        };

        for (var index = 0; index < sequentialWorkflow.ParticipantOrder.Count; index++)
        {
            var participantId = sequentialWorkflow.ParticipantOrder[index];
            if (!participantsById.TryGetValue(participantId, out var participant))
            {
                continue;
            }

            var prefix = $"workflow.participant.{index}";
            metadata[$"{prefix}.id"] = participant.ParticipantId;
            metadata[$"{prefix}.name"] = participant.DisplayName;
            metadata[$"{prefix}.definition.kind"] = participant.Source is ReferencedParticipantSource referenced
                ? referenced.Reference.Kind.ToString()
                : AgentDefinitionKind.SavedAgent.ToString();
            metadata[$"{prefix}.definition.id"] = participant.Source is ReferencedParticipantSource referencedSource
                ? referencedSource.Reference.Id
                : participant.ParticipantId;

            if (index < outputMessages.Count)
            {
                metadata[$"{prefix}.result.author"] = outputMessages[index].Author;
            }
        }

        foreach (var pair in finalParticipantResult.Metadata)
        {
            metadata[$"workflow.participant.{sequentialWorkflow.ParticipantOrder.Count - 1}.metadata.{pair.Key}"] =
                pair.Value;
        }

        if (!string.IsNullOrWhiteSpace(finalParticipantResult.FinalMessageId))
        {
            metadata[$"workflow.participant.{sequentialWorkflow.ParticipantOrder.Count - 1}.result.messageId"] =
                finalParticipantResult.FinalMessageId;
        }

        return metadata;
    }

    private static bool IsFinalParticipant(
        SequentialWorkflowDefinition workflow,
        string participantId) =>
        string.Equals(
            workflow.ParticipantOrder.LastOrDefault(),
            participantId,
            StringComparison.OrdinalIgnoreCase);
}

internal static class SequentialParticipantRequestBuilder
{
    public static AgentRuntimeRunRequest Build(
        AgentRuntimeRunRequest workflowRequest,
        ResolvedWorkflowParticipant participant,
        AgentRunResult? previousResult)
    {
        if (previousResult is null)
        {
            return workflowRequest;
        }

        var originalUserMessage = workflowRequest.Messages
            .LastOrDefault(static message => message.Role == AgentMessageRole.User)
            ?.Content
            ?.Trim();
        var messages = new List<AgentInputMessage>();

        if (!string.IsNullOrWhiteSpace(originalUserMessage))
        {
            messages.Add(new AgentInputMessage(
                AgentMessageRole.User,
                $"Original request:\n{originalUserMessage}"));
        }

        messages.Add(new AgentInputMessage(
            AgentMessageRole.User,
            $"Previous participant final response:\n{previousResult.FinalMessage.Content}"));

        return new AgentRuntimeRunRequest
        {
            Messages = messages,
            Inputs = workflowRequest.Inputs,
            Attachments = []
        };
    }
}
