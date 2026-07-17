using System.Threading.Channels;
using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services.BuiltIn;
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
    IWorkflowAgentDraftMaterializer workflowAgentDraftMaterializer,
    OrchestrationWorkflowSessionBootstrapper sessionBootstrapper,
    OrchestrationWorkflowTurnCoordinator turnCoordinator,
    OrchestrationWorkflowPassExecutor passExecutor,
    TaskSessionStore taskSessionStore,
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
        var workflow = compiled.Workflow
            ?? throw new InvalidOperationException("Workflow compilation did not return a workflow definition.");
        var materialized = await workflowAgentDraftMaterializer.MaterializeAsync(workflow, cancellationToken);
        var resolvedAgents = materialized.Agents
            .Select(agent => ResolveWorkflowAgent(agent, context))
            .ToList();

        return new WorkflowAgentRuntime(
            new AgentRuntimeDescriptor(
                savedWorkflow.Id.ToString("D"),
                savedWorkflow.DisplayName,
                savedWorkflow.Description,
                AgentRuntimeKind.WorkflowAgent),
            materialized,
            resolvedAgents,
            context.Configuration,
            sessionBootstrapper,
            turnCoordinator,
            passExecutor,
            taskSessionStore,
            logger);
    }

    private static ResolvedChatAgent ResolveWorkflowAgent(
        AgentWorkflowAgentDefinition workflowAgent,
        AgentRuntimeCreationContext context)
    {
        var draft = workflowAgent.AgentDraft
            ?? throw new InvalidOperationException(
                $"Workflow agent '{workflowAgent.Id}' has no materialized agent draft.");
        var uiSelection = context.DefaultModel is null
            ? new ServerModelSelection(null, null)
            : new ServerModelSelection(context.DefaultModel.ServerId, context.DefaultModel.ModelName);

        if (!ModelSelectionHelper.TryGetEffectiveModel(
                new ServerModelSelection(draft.LlmId, draft.ModelName),
                uiSelection,
                out var model))
        {
            throw new InvalidOperationException(
                $"Model selection for workflow agent '{workflowAgent.Id}' is incomplete.");
        }

        return ResolvedChatAgentFactory.Resolve(draft, model);
    }
}

internal sealed class WorkflowAgentRuntime(
    AgentRuntimeDescriptor descriptor,
    IOrchestrationWorkflowDefinition workflow,
    IReadOnlyList<ResolvedChatAgent> agents,
    AppChatConfiguration configuration,
    OrchestrationWorkflowSessionBootstrapper sessionBootstrapper,
    OrchestrationWorkflowTurnCoordinator turnCoordinator,
    OrchestrationWorkflowPassExecutor passExecutor,
    TaskSessionStore taskSessionStore,
    ILogger logger) : IAgentRuntime
{
    public AgentRuntimeDescriptor Descriptor { get; } = descriptor;

    public async IAsyncEnumerable<AgentRunEvent> RunAsync(
        AgentRuntimeRunRequest request,
        AgentRunContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<AgentRunEvent>();
        var completedMessages = new List<OrchestrationCompletedAssistantMessage>();

        var producer = ProduceAsync(
            request,
            context,
            completedMessages,
            channel.Writer,
            cancellationToken);

        await foreach (var runEvent in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return runEvent;
        }

        await producer;
    }

    private async Task ProduceAsync(
        AgentRuntimeRunRequest request,
        AgentRunContext context,
        List<OrchestrationCompletedAssistantMessage> completedMessages,
        ChannelWriter<AgentRunEvent> writer,
        CancellationToken cancellationToken)
    {
        var chatMessages = new List<IAppChatMessage>();
        var speakerIdsByMessageId = new Dictionary<Guid, string?>();
        var assistantSpeakerIds = new List<string>();
        var activeStreams = new Dictionary<Guid, StreamingAppChatMessage>();
        var activeSpeakerIdsByStreamId = new Dictionary<Guid, string?>();
        var streamContentLengths = new Dictionary<Guid, int>();
        var emittedCompletedMessageIds = new HashSet<Guid>();
        var agentIdsByExecutorId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var agentIdsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var agentNamesById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var currentUserMessageIndex = FindCurrentUserMessageIndex(request.Messages);
            var userMessage = currentUserMessageIndex >= 0
                ? request.Messages[currentUserMessageIndex]
                : null;
            if (userMessage is null || string.IsNullOrWhiteSpace(userMessage.Content))
            {
                await writer.WriteAsync(new AgentRunFailed(new AgentRunError(
                    "invalid_input",
                    "At least one non-empty user message is required.",
                    false)), cancellationToken);
                return;
            }

            if (request.Messages.Skip(currentUserMessageIndex + 1).Any())
            {
                await writer.WriteAsync(new AgentRunFailed(new AgentRunError(
                    "invalid_input",
                    "Messages after the current user message are not supported.",
                    false)), cancellationToken);
                return;
            }

            if (!TryBuildStartInputsWithAttachments(
                    request,
                    out var startInputs,
                    out var attachmentError))
            {
                await writer.WriteAsync(new AgentRunFailed(new AgentRunError(
                    "invalid_input",
                    attachmentError ?? "Workflow attachments could not be mapped to start inputs.",
                    false)), cancellationToken);
                return;
            }

            var bootstrap = await sessionBootstrapper.BootstrapAsync(
                new OrchestrationWorkflowSessionStartRequest
                {
                    Workflow = workflow,
                    Agents = agents,
                    Configuration = configuration,
                    SessionTitle = Descriptor.Name,
                    SessionDescription = Descriptor.Description,
                    StartInputs = startInputs
                },
                cancellationToken);

            foreach (var runtimeAgent in bootstrap.RuntimeAgents)
            {
                RegisterAgentIdentity(
                    runtimeAgent.AgentId,
                    runtimeAgent.AgentName,
                    runtimeAgent.ExecutorId,
                    agentIdsByExecutorId,
                    agentIdsByName,
                    agentNamesById);
            }

            var taskSessionId = bootstrap.TaskSessionId;
            var userContent = BuildWorkflowUserMessage(
                request.Messages.Take(currentUserMessageIndex),
                userMessage.Content);
            var userChatMessage = new AppChatMessage(userContent, DateTime.Now, AppChatRole.User);
            await AddMessageAsync(userChatMessage, chatMessages);
            await taskSessionStore.AppendTurnAsync(
                taskSessionId,
                "user",
                OrchestrationWorkflowConversationBuilder.BuildUserMessage(userContent, []),
                "user",
                cancellationToken);

            await turnCoordinator.RunAsync(
                new OrchestrationWorkflowTurnExecutionRequest
                {
                    WorkflowDisplayName = workflow.DisplayName,
                    Execution = workflow.Execution,
                    IsExecutionCompleteAsync = cancellation => IsWorkflowExecutionCompleteAsync(
                        workflow.Execution,
                        taskSessionId,
                        cancellation),
                    ExecutePassAsync = cancellation => passExecutor.ExecuteAsync(
                        new OrchestrationWorkflowPassExecutionRequest
                        {
                            Workflow = workflow,
                            SessionId = taskSessionId,
                            Messages = chatMessages.ToList(),
                            AssistantSpeakerIds = assistantSpeakerIds.ToList(),
                            RuntimeAgentsById = bootstrap.RuntimeAgents.ToDictionary(
                                static agent => agent.AgentId,
                                static agent => agent.RuntimeAgent,
                                StringComparer.OrdinalIgnoreCase),
                            EventStreamContext = new OrchestrationWorkflowEventStreamContext
                            {
                                ModelName = configuration.ModelName,
                                Workflow = workflow,
                                Messages = chatMessages.ToList(),
                                SpeakerIdsByMessageId = speakerIdsByMessageId,
                                ActiveStreams = activeStreams,
                                ActiveSpeakerIdsByStreamId = activeSpeakerIdsByStreamId,
                                AgentIdsByExecutorId = agentIdsByExecutorId,
                                AgentIdsByName = agentIdsByName,
                                AgentNamesById = agentNamesById,
                                AddMessageAsync = message => AddMessageAsync(message, chatMessages),
                                ReplaceMessage = (source, replacement) => ReplaceMessage(
                                    source,
                                    replacement,
                                    chatMessages),
                                NotifyMessageUpdatedAsync = (message, isFinal) => NotifyMessageAsync(
                                    message,
                                    isFinal,
                                    writer,
                                    streamContentLengths,
                                    emittedCompletedMessageIds,
                                    cancellation)
                            }
                        },
                        cancellation),
                    ProcessCompletedAssistantMessagesAsync = async (messages, cancellation) =>
                    {
                        foreach (var completedMessage in messages)
                        {
                            completedMessages.Add(completedMessage);
                            await taskSessionStore.AppendTurnAsync(
                                taskSessionId,
                                "assistant",
                                completedMessage.Message.Content,
                                completedMessage.SpeakerId,
                                cancellation);

                            speakerIdsByMessageId[completedMessage.Message.Id] = completedMessage.SpeakerId;
                            if (!string.IsNullOrWhiteSpace(completedMessage.SpeakerId))
                            {
                                assistantSpeakerIds.Add(completedMessage.SpeakerId);
                            }
                        }
                    },
                    HandleAssistantErrorAsync = text => throw new WorkflowAssistantErrorException(text)
                },
                cancellationToken);

            foreach (var completedMessage in completedMessages)
            {
                await PublishCompletedMessageAsync(
                    completedMessage.Message,
                    writer,
                    emittedCompletedMessageIds,
                    cancellationToken);
            }

            var final = await ResolveFinalMessageAsync(
                taskSessionId,
                completedMessages,
                cancellationToken);
            if (final is null)
            {
                await writer.WriteAsync(new AgentRunFailed(new AgentRunError(
                    "workflow_produced_no_result",
                    "Workflow produced no final assistant result.",
                    false)), cancellationToken);
                return;
            }

            await writer.WriteAsync(new AgentRunCompleted(new AgentRunResult
            {
                FinalMessage = final.Value.Message,
                FinalMessageId = final.Value.MessageId,
                Messages = completedMessages
                    .Select(static message => ToOutputMessage(message.Message))
                    .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
                    .ToList(),
                Metadata = final.Value.Metadata
            }), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkflowAssistantErrorException ex)
        {
            await writer.WriteAsync(new AgentRunFailed(new AgentRunError(
                "execution_failed",
                ex.Message,
                true)), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Workflow agent runtime failed. RunId={RunId}, WorkflowId={WorkflowId}, WorkflowName={WorkflowName}, WorkflowKind={WorkflowKind}, ParticipantCount={ParticipantCount}",
                context.RunId,
                Descriptor.Id,
                Descriptor.Name,
                workflow.Kind,
                workflow.Agents.Count);
            await writer.WriteAsync(new AgentRunFailed(new AgentRunError(
                "execution_failed",
                ex.Message,
                true,
                ex)), CancellationToken.None);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task<bool> IsWorkflowExecutionCompleteAsync(
        AgentWorkflowExecutionDefinition execution,
        string taskSessionId,
        CancellationToken cancellationToken)
    {
        var snapshot = await taskSessionStore.GetSessionAsync(taskSessionId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(execution.CompletionPhase) &&
            string.Equals(snapshot.Phase, execution.CompletionPhase, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(execution.CompletionSummaryLabel) &&
            snapshot.Summaries.Any(summary =>
                string.Equals(summary.Label, execution.CompletionSummaryLabel, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private async Task<(string MessageId, AgentOutputMessage Message, IReadOnlyDictionary<string, string> Metadata)?> ResolveFinalMessageAsync(
        string taskSessionId,
        IReadOnlyList<OrchestrationCompletedAssistantMessage> messages,
        CancellationToken cancellationToken)
    {
        var nonEmptyMessages = messages
            .Where(static message => !string.IsNullOrWhiteSpace(message.Message.Content))
            .ToList();
        if (nonEmptyMessages.Count == 0)
        {
            return null;
        }

        var finalMessage = workflow switch
        {
            SequentialWorkflowDefinition sequential => ResolveSequentialFinal(sequential, nonEmptyMessages),
            ConcurrentWorkflowDefinition concurrent => ResolveConcurrentFinal(concurrent, nonEmptyMessages),
            GroupChatWorkflowDefinition => await ResolveGroupChatFinalAsync(
                taskSessionId,
                nonEmptyMessages,
                cancellationToken),
            AgentWorkflowDefinition => nonEmptyMessages.Last(),
            _ => nonEmptyMessages.Last()
        };

        if (finalMessage is null)
        {
            return null;
        }

        var metadata = new Dictionary<string, string>
        {
            ["workflowKind"] = workflow.Kind
        };

        if (!string.IsNullOrWhiteSpace(finalMessage.SpeakerId))
        {
            metadata["finalParticipantId"] = finalMessage.SpeakerId;
        }

        if (!string.IsNullOrWhiteSpace(finalMessage.Message.AgentName))
        {
            metadata["finalParticipantName"] = finalMessage.Message.AgentName;
        }

        return (
            finalMessage.Message.Id.ToString("N"),
            new AgentOutputMessage(Descriptor.Name, finalMessage.Message.Content),
            metadata);
    }

    private static OrchestrationCompletedAssistantMessage? ResolveSequentialFinal(
        SequentialWorkflowDefinition workflow,
        IReadOnlyList<OrchestrationCompletedAssistantMessage> messages)
    {
        var lastAgentId = workflow.AgentOrder.LastOrDefault();
        if (!string.IsNullOrWhiteSpace(lastAgentId))
        {
            var fromLastAgent = messages.LastOrDefault(message =>
                string.Equals(message.SpeakerId, lastAgentId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(message.Message.AgentId, lastAgentId, StringComparison.OrdinalIgnoreCase));
            if (fromLastAgent is not null)
            {
                return fromLastAgent;
            }
        }

        return messages.LastOrDefault();
    }

    private OrchestrationCompletedAssistantMessage? ResolveConcurrentFinal(
        ConcurrentWorkflowDefinition workflow,
        IReadOnlyList<OrchestrationCompletedAssistantMessage> messages)
    {
        if (workflow.Aggregation.Kind == ConcurrentWorkflowAggregationKind.ConcatenateAllMessages)
        {
            return new OrchestrationCompletedAssistantMessage(
                new AppChatMessage(
                    string.Join(Environment.NewLine + Environment.NewLine, messages.Select(static message => message.Message.Content)),
                    DateTime.Now,
                    AppChatRole.Assistant,
                    agentName: Descriptor.Name),
                Descriptor.Id);
        }

        var sections = new List<string>();
        foreach (var participantId in workflow.ParticipantAgentIds)
        {
            var message = messages.LastOrDefault(candidate =>
                string.Equals(candidate.SpeakerId, participantId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Message.AgentId, participantId, StringComparison.OrdinalIgnoreCase));
            if (message is null)
            {
                continue;
            }

            var heading = string.IsNullOrWhiteSpace(message.Message.AgentName)
                ? participantId
                : message.Message.AgentName;
            sections.Add($"## {heading}{Environment.NewLine}{message.Message.Content}");
        }

        return sections.Count == 0
            ? null
            : new OrchestrationCompletedAssistantMessage(
                new AppChatMessage(
                    string.Join(Environment.NewLine + Environment.NewLine, sections),
                    DateTime.Now,
                    AppChatRole.Assistant,
                    agentName: Descriptor.Name),
                Descriptor.Id);
    }

    private async Task<OrchestrationCompletedAssistantMessage?> ResolveGroupChatFinalAsync(
        string taskSessionId,
        IReadOnlyList<OrchestrationCompletedAssistantMessage> messages,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(workflow.Execution.CompletionSummaryLabel))
        {
            var snapshot = await taskSessionStore.GetSummaryAsync(
                taskSessionId,
                workflow.Execution.CompletionSummaryLabel,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(snapshot.Markdown))
            {
                return new OrchestrationCompletedAssistantMessage(
                    new AppChatMessage(
                        snapshot.Markdown,
                        DateTime.Now,
                        AppChatRole.Assistant,
                        agentName: Descriptor.Name),
                    Descriptor.Id);
            }
        }

        return messages.LastOrDefault();
    }

    private static async Task NotifyMessageAsync(
        IAppChatMessage message,
        bool isFinal,
        ChannelWriter<AgentRunEvent> writer,
        Dictionary<Guid, int> streamContentLengths,
        HashSet<Guid> emittedCompletedMessageIds,
        CancellationToken cancellationToken)
    {
        if (message.Role != AppChatRole.Assistant)
        {
            return;
        }

        if (!isFinal)
        {
            var previousLength = streamContentLengths.GetValueOrDefault(message.Id);
            var content = message.Content ?? string.Empty;
            if (content.Length > previousLength)
            {
                await writer.WriteAsync(
                    new AgentTextDelta(
                        message.Id.ToString("N"),
                        string.IsNullOrWhiteSpace(message.AgentName) ? "assistant" : message.AgentName,
                        content[previousLength..]),
                    cancellationToken);
                streamContentLengths[message.Id] = content.Length;
            }

            return;
        }

        await PublishCompletedMessageAsync(
            message,
            writer,
            emittedCompletedMessageIds,
            cancellationToken);
    }

    private static async Task PublishCompletedMessageAsync(
        IAppChatMessage message,
        ChannelWriter<AgentRunEvent> writer,
        HashSet<Guid> emittedCompletedMessageIds,
        CancellationToken cancellationToken)
    {
        if (!emittedCompletedMessageIds.Add(message.Id) ||
            string.IsNullOrWhiteSpace(message.Content))
        {
            return;
        }

        await writer.WriteAsync(
            new AgentMessageCompleted(message.Id.ToString("N"), ToOutputMessage(message)),
            cancellationToken);
    }

    private static AgentOutputMessage ToOutputMessage(IAppChatMessage message) =>
        new(
            string.IsNullOrWhiteSpace(message.AgentName) ? "assistant" : message.AgentName,
            message.Content);

    private static Task AddMessageAsync(
        IAppChatMessage message,
        List<IAppChatMessage> messages)
    {
        if (messages.All(existing => existing.Id != message.Id))
        {
            messages.Add(message);
        }

        return Task.CompletedTask;
    }

    private static void ReplaceMessage(
        IAppChatMessage source,
        IAppChatMessage replacement,
        List<IAppChatMessage> messages)
    {
        var index = messages.FindIndex(message => message.Id == source.Id);
        if (index >= 0)
        {
            messages[index] = replacement;
            return;
        }

        messages.Add(replacement);
    }

    private static void RegisterAgentIdentity(
        string agentId,
        string agentName,
        string? executorId,
        Dictionary<string, string> agentIdsByExecutorId,
        Dictionary<string, string> agentIdsByName,
        Dictionary<string, string> agentNamesById)
    {
        agentIdsByExecutorId[agentId] = agentId;
        if (!string.IsNullOrWhiteSpace(executorId))
        {
            agentIdsByExecutorId[executorId] = agentId;
        }

        agentIdsByExecutorId[agentName] = agentId;
        agentIdsByName[agentName] = agentId;
        agentNamesById[agentId] = agentName;
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

    private sealed class WorkflowAssistantErrorException(string message) : Exception(message);
}
