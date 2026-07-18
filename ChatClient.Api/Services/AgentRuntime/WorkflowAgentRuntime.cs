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
    IServiceProvider serviceProvider,
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
        var workflowReference = new AgentDefinitionReference(
            AgentDefinitionKind.SavedWorkflow,
            savedWorkflow.Id.ToString("D"));
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
            workflowReference,
            workflow,
            resolvedParticipants,
            runtimeParticipants,
            context.Configuration,
            context,
            headlessWorkflowRunner,
            new LazyWorkflowParticipantExecutor(serviceProvider),
            logger);
    }

}

public interface IWorkflowParticipantExecutor
{
    IAsyncEnumerable<AgentRunEvent> RunAsync(
        ResolvedWorkflowParticipant participant,
        AgentRuntimeRunRequest request,
        AgentRuntimeCreationContext creationContext,
        AgentRunContext runContext,
        CancellationToken cancellationToken = default);
}

public sealed class WorkflowParticipantExecutor(
    IAgentRunner agentRunner,
    IInlineLlmAgentRuntimeFactory inlineRuntimeFactory,
    IAgentRuntimeProtocolExecutor protocolExecutor) : IWorkflowParticipantExecutor
{
    public async IAsyncEnumerable<AgentRunEvent> RunAsync(
        ResolvedWorkflowParticipant participant,
        AgentRuntimeRunRequest request,
        AgentRuntimeCreationContext creationContext,
        AgentRunContext runContext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        switch (participant.Source)
        {
            case ReferencedParticipantSource referenced:
                await foreach (var runEvent in agentRunner.RunAsync(
                                   referenced.Reference,
                                   request,
                                   creationContext,
                                   runContext,
                                   cancellationToken))
                {
                    yield return runEvent;
                }

                yield break;

            case MaterializedLlmParticipantSource materialized:
                var runtime = inlineRuntimeFactory.Create(
                    new AgentRuntimeDescriptor(
                        participant.ParticipantId,
                        participant.DisplayName,
                        participant.Summary,
                        AgentRuntimeKind.LlmAgent),
                    materialized.Agent,
                    creationContext);
                await foreach (var runEvent in protocolExecutor.RunAsync(
                                   runtime,
                                   request,
                                   runContext,
                                   cancellationToken))
                {
                    yield return runEvent;
                }

                yield break;

            default:
                yield return new AgentRunFailed(new AgentRunError(
                    "invalid_workflow",
                    $"Workflow participant '{participant.ParticipantId}' has no executable source.",
                    false));
                yield break;
        }
    }
}

file sealed class LazyWorkflowParticipantExecutor(
    IServiceProvider serviceProvider) : IWorkflowParticipantExecutor
{
    public IAsyncEnumerable<AgentRunEvent> RunAsync(
        ResolvedWorkflowParticipant participant,
        AgentRuntimeRunRequest request,
        AgentRuntimeCreationContext creationContext,
        AgentRunContext runContext,
        CancellationToken cancellationToken = default)
    {
        var executor = serviceProvider.GetRequiredService<IWorkflowParticipantExecutor>();
        return executor.RunAsync(
            participant,
            request,
            creationContext,
            runContext,
            cancellationToken);
    }
}

internal sealed class WorkflowAgentRuntime(
    AgentRuntimeDescriptor descriptor,
    AgentDefinitionReference workflowReference,
    IOrchestrationWorkflowDefinition workflow,
    IReadOnlyList<ResolvedWorkflowParticipant> participants,
    IReadOnlyList<WorkflowRuntimeParticipant> runtimeParticipants,
    AppChatConfiguration configuration,
    AgentRuntimeCreationContext creationContext,
    IHeadlessWorkflowRunner headlessWorkflowRunner,
    IWorkflowParticipantExecutor participantExecutor,
    ILogger logger) : IAgentRuntime
{
    private const int DefaultMaximumWorkflowNestingDepth = 8;

    public AgentRuntimeDescriptor Descriptor { get; } = descriptor;

    public WorkflowAgentRuntime(
        AgentRuntimeDescriptor descriptor,
        IOrchestrationWorkflowDefinition workflow,
        IReadOnlyList<ResolvedChatAgent> agents,
        AppChatConfiguration configuration,
        IHeadlessWorkflowRunner headlessWorkflowRunner,
        ILogger logger)
        : this(
            descriptor,
            new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, descriptor.Id),
            workflow,
            workflow.Participants.Select(static participant => new ResolvedWorkflowParticipant
            {
                ParticipantId = participant.Id,
                DisplayName = string.IsNullOrWhiteSpace(participant.Role) ? participant.Id : participant.Role,
                Summary = participant.Summary,
                RuntimeKind = AgentRuntimeKind.LlmAgent,
                Source = participant.Source is InlineAgentParticipantSource inline
                    ? new MaterializedLlmParticipantSource(inline.Agent)
                    : throw new InvalidOperationException(
                        $"Workflow participant '{participant.Id}' has no inline agent source.")
            }).ToList(),
            agents.Select(static agent => new WorkflowRuntimeParticipant
            {
                Id = agent.Agent.AgentId,
                DisplayName = agent.Agent.AgentName,
                Summary = agent.Agent.Summary,
                Runtime = new MissingWorkflowRuntime()
            }).ToList(),
            configuration,
            new AgentRuntimeCreationContext
            {
                Configuration = configuration
            },
            headlessWorkflowRunner,
            new MissingWorkflowParticipantExecutor(),
            logger)
    {
    }

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
            SessionDescription = Descriptor.Description
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

        await using (session!)
        {
            var events = session.RunTurnAsync(new HeadlessWorkflowTurnRequest
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
            "execution_failed",
            "Workflow execution failed.",
            true,
            ex));
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
        if (!ValidateRuntimeNesting(context, out var nestingFailure))
        {
            yield return nestingFailure!;
            yield break;
        }

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

            if (DetectCycle(participant, context, out var cycleFailure))
            {
                yield return cycleFailure!;
                yield break;
            }

            var childContext = CreateChildContext(context, participant);
            var participantRequest = SequentialParticipantRequestBuilder.Build(
                request,
                participant,
                previousResult);
            AgentRunResult? terminalResult = null;
            AgentRunFailed? terminalFailure = null;

            await foreach (var participantEvent in participantExecutor.RunAsync(
                               participant,
                               participantRequest,
                               creationContext,
                               childContext,
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
        yield return new AgentMessageCompleted(messageId, finalMessage);
        yield return new AgentRunCompleted(new AgentRunResult
        {
            FinalMessage = finalMessage,
            FinalMessageId = messageId,
            Messages = [finalMessage],
            Metadata = new Dictionary<string, string>
            {
                ["runtime.kind"] = "workflow",
                ["workflow.id"] = Descriptor.Id,
                ["workflow.name"] = Descriptor.Name,
                ["workflowKind"] = workflow.Kind
            }
        });
    }

    private AgentRunContext CreateChildContext(
        AgentRunContext parent,
        ResolvedWorkflowParticipant participant)
    {
        var path = EnsureCurrentWorkflowInPath(parent.DefinitionPath)
            .Concat(participant.Source is ReferencedParticipantSource referenced ? [referenced.Reference] : [])
            .ToList();

        return new AgentRunContext
        {
            RunId = Guid.NewGuid().ToString("N"),
            ParentRunId = parent.RunId,
            ConversationId = parent.ConversationId,
            DefinitionPath = path
        };
    }

    private bool ValidateRuntimeNesting(
        AgentRunContext context,
        out AgentRunFailed? failure)
    {
        var workflowDepth = EnsureCurrentWorkflowInPath(context.DefinitionPath)
            .Count(static reference => reference.Kind == AgentDefinitionKind.SavedWorkflow);
        if (workflowDepth <= DefaultMaximumWorkflowNestingDepth)
        {
            failure = null;
            return true;
        }

        failure = new AgentRunFailed(new AgentRunError(
            "workflow_nesting_limit_exceeded",
            $"Workflow nesting limit exceeded. Maximum depth is {DefaultMaximumWorkflowNestingDepth}.",
            false));
        return false;
    }

    private bool DetectCycle(
        ResolvedWorkflowParticipant participant,
        AgentRunContext context,
        out AgentRunFailed? failure)
    {
        failure = null;
        if (participant.Source is not ReferencedParticipantSource referenced ||
            referenced.Reference.Kind != AgentDefinitionKind.SavedWorkflow)
        {
            return false;
        }

        var path = EnsureCurrentWorkflowInPath(context.DefinitionPath);
        if (!path.Any(reference => SameReference(reference, referenced.Reference)))
        {
            return false;
        }

        var cyclePath = path
            .Where(static reference => reference.Kind == AgentDefinitionKind.SavedWorkflow)
            .Concat([referenced.Reference])
            .Select(static reference => reference.Id)
            .ToList();

        failure = new AgentRunFailed(new AgentRunError(
            "workflow_cycle_detected",
            $"Workflow dependency cycle detected: {string.Join(" -> ", cyclePath)}.",
            false));
        return true;
    }

    private static bool SameReference(
        AgentDefinitionReference left,
        AgentDefinitionReference right) =>
        left.Kind == right.Kind &&
        string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<AgentDefinitionReference> EnsureCurrentWorkflowInPath(
        IReadOnlyList<AgentDefinitionReference> path)
    {
        if (path.Any(reference => SameReference(reference, workflowReference)))
        {
            return path;
        }

        return path.Concat([workflowReference]).ToList();
    }

    private static bool IsFinalParticipant(
        SequentialWorkflowDefinition workflow,
        string participantId) =>
        string.Equals(
            workflow.ParticipantOrder.LastOrDefault(),
            participantId,
            StringComparison.OrdinalIgnoreCase);
}

file sealed class MissingWorkflowParticipantExecutor : IWorkflowParticipantExecutor
{
    public IAsyncEnumerable<AgentRunEvent> RunAsync(
        ResolvedWorkflowParticipant participant,
        AgentRuntimeRunRequest request,
        AgentRuntimeCreationContext creationContext,
        AgentRunContext runContext,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Sequential workflow participants require a configured participant executor.");
}

file sealed class MissingWorkflowRuntime : IAgentRuntime
{
    public AgentRuntimeDescriptor Descriptor { get; } =
        new("missing", "Missing workflow runtime", string.Empty, AgentRuntimeKind.LlmAgent);

    public IAsyncEnumerable<AgentRunEvent> RunAsync(
        AgentRuntimeRunRequest request,
        AgentRunContext context,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This workflow runtime placeholder is not executable.");
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
