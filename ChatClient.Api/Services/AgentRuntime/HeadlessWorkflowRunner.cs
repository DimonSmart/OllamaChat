using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Runtime;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using System.Threading.Channels;

namespace ChatClient.Api.Services.AgentRuntime;

public interface IWorkflowExecutionEngine
{
    IAsyncEnumerable<AgentRunEvent> ExecuteAsync(
        WorkflowExecutionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record WorkflowExecutionRequest
{
    public required IOrchestrationWorkflowDefinition Workflow { get; init; }

    public IReadOnlyList<WorkflowRuntimeParticipant> Participants { get; init; } = [];

    public IReadOnlyList<ResolvedWorkflowParticipant> ResolvedParticipants { get; init; } = [];

    public IReadOnlyList<ResolvedChatAgent> Agents { get; init; } = [];

    public IWorkflowParticipantInvoker? ParticipantInvoker { get; init; }

    public required AppChatConfiguration Configuration { get; init; }

    public AgentRuntimeCreationContext? CreationContext { get; init; }

    public AgentRunContext? ParentRunContext { get; init; }

    public IReadOnlyList<OrchestrationWorkflowStartInputValue> StartInputs { get; init; } = [];

    public required string SessionTitle { get; init; }

    public string SessionDescription { get; init; } = string.Empty;
    public string? UserMessage { get; init; }
}

public sealed class WorkflowExecutionEngine(
    OrchestrationWorkflowSessionBootstrapper sessionBootstrapper,
    OrchestrationWorkflowTurnCoordinator turnCoordinator,
    OrchestrationWorkflowPassExecutor passExecutor,
    TaskSessionStore taskSessionStore,
    ILogger<WorkflowExecutionEngine> logger) : IWorkflowExecutionEngine
{
    public async IAsyncEnumerable<AgentRunEvent> ExecuteAsync(
        WorkflowExecutionRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bootstrap = await sessionBootstrapper.BootstrapAsync(
            new OrchestrationWorkflowSessionStartRequest
            {
                Workflow = request.Workflow,
                Participants = request.Participants,
                ResolvedParticipants = request.ResolvedParticipants,
                Agents = request.Agents,
                Configuration = request.Configuration,
                CreationContext = request.CreationContext,
                ParentRunContext = request.ParentRunContext,
                ParticipantInvoker = request.ParticipantInvoker,
                SessionTitle = request.SessionTitle,
                SessionDescription = request.SessionDescription,
                StartInputs = request.StartInputs
            },
            cancellationToken);

        var session = new WorkflowExecutionSession(
            bootstrap,
            turnCoordinator,
            passExecutor,
            taskSessionStore,
            logger);

        await foreach (var runEvent in session.ExecuteAsync(request, cancellationToken))
        {
            yield return runEvent;
        }
    }

    private sealed class WorkflowExecutionSession(
        OrchestrationWorkflowSessionBootstrapResult bootstrap,
        OrchestrationWorkflowTurnCoordinator turnCoordinator,
        OrchestrationWorkflowPassExecutor passExecutor,
        TaskSessionStore taskSessionStore,
        ILogger logger)
    {
        private readonly List<IAppChatMessage> _chatMessages = [];
        private readonly Dictionary<Guid, string?> _speakerIdsByMessageId = [];
        private readonly List<string> _assistantSpeakerIds = [];
        private readonly Dictionary<Guid, StreamingAppChatMessage> _activeStreams = [];
        private readonly Dictionary<Guid, string?> _activeSpeakerIdsByStreamId = [];
        private readonly Dictionary<Guid, int> _streamContentLengths = [];
        private readonly HashSet<Guid> _emittedCompletedMessageIds = [];
        private readonly HashSet<string> _emittedCompletedMessageContents = [];
        private readonly Dictionary<string, string> _agentIdsByExecutorId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _agentIdsByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _agentNamesById = new(StringComparer.OrdinalIgnoreCase);
        public string TaskSessionId => bootstrap.TaskSessionId;

        public async IAsyncEnumerable<AgentRunEvent> ExecuteAsync(
            WorkflowExecutionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var channel = Channel.CreateUnbounded<AgentRunEvent>();
            var completedMessages = new List<OrchestrationCompletedAssistantMessage>();

            var producer = ProduceTurnAsync(
                request,
                completedMessages,
                channel.Writer,
                cancellationToken);

            await foreach (var runEvent in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return runEvent;
            }

            await producer;
        }

        private async Task ProduceTurnAsync(
            WorkflowExecutionRequest request,
            List<OrchestrationCompletedAssistantMessage> completedMessages,
            ChannelWriter<AgentRunEvent> writer,
            CancellationToken cancellationToken)
        {
            var workflowRequest = bootstrap.Request;
            try
            {
                foreach (var runtimeAgent in bootstrap.RuntimeAgents)
                {
                    RegisterAgentIdentity(
                        runtimeAgent.AgentId,
                        runtimeAgent.AgentName,
                        runtimeAgent.ExecutorId,
                        _agentIdsByExecutorId,
                        _agentIdsByName,
                        _agentNamesById);
                }

                if (!string.IsNullOrWhiteSpace(request.UserMessage))
                {
                    var userChatMessage = new AppChatMessage(
                        request.UserMessage,
                        DateTime.Now,
                        AppChatRole.User);
                    await AddMessageAsync(userChatMessage, _chatMessages);
                }

                await turnCoordinator.RunAsync(
                    new OrchestrationWorkflowTurnExecutionRequest
                    {
                        WorkflowDisplayName = workflowRequest.Workflow.DisplayName,
                        Execution = workflowRequest.Workflow.Execution,
                        IsExecutionCompleteAsync = cancellation => IsWorkflowExecutionCompleteAsync(
                            workflowRequest.Workflow.Execution,
                            TaskSessionId,
                            taskSessionStore,
                            cancellation),
                        ExecutePassAsync = cancellation => passExecutor.ExecuteAsync(
                            new OrchestrationWorkflowPassExecutionRequest
                            {
                                Workflow = workflowRequest.Workflow,
                                SessionId = TaskSessionId,
                                Messages = _chatMessages.ToList(),
                                AssistantSpeakerIds = _assistantSpeakerIds.ToList(),
                                RuntimeAgentsById = bootstrap.RuntimeAgents.ToDictionary(
                                    static agent => agent.AgentId,
                                    static agent => agent.RuntimeAgent,
                                    StringComparer.OrdinalIgnoreCase),
                                EventStreamContext = new OrchestrationWorkflowEventStreamContext
                                {
                                    ModelName = workflowRequest.Configuration.ModelName,
                                    Workflow = workflowRequest.Workflow,
                                    Messages = _chatMessages.ToList(),
                                    SpeakerIdsByMessageId = _speakerIdsByMessageId,
                                    ActiveStreams = _activeStreams,
                                    ActiveSpeakerIdsByStreamId = _activeSpeakerIdsByStreamId,
                                    AgentIdsByExecutorId = _agentIdsByExecutorId,
                                    AgentIdsByName = _agentIdsByName,
                                    AgentNamesById = _agentNamesById,
                                    AddMessageAsync = message => AddMessageAsync(message, _chatMessages),
                                    ReplaceMessage = (source, replacement) => ReplaceMessage(
                                        source,
                                        replacement,
                                        _chatMessages),
                                    NotifyMessageUpdatedAsync = (message, isFinal) => NotifyMessageAsync(
                                        message,
                                        isFinal,
                                        writer,
                                        _streamContentLengths,
                                        _emittedCompletedMessageIds,
                                        _emittedCompletedMessageContents,
                                        cancellation)
                                }
                            },
                            cancellation),
                        ProcessCompletedAssistantMessagesAsync = async (messages, cancellation) =>
                        {
                            foreach (var completedMessage in messages)
                            {
                                completedMessages.Add(completedMessage);
                                _speakerIdsByMessageId[completedMessage.Message.Id] = completedMessage.SpeakerId;
                                if (!string.IsNullOrWhiteSpace(completedMessage.SpeakerId))
                                {
                                    _assistantSpeakerIds.Add(completedMessage.SpeakerId);
                                }
                            }
                        },
                        HandleAssistantErrorAsync = text => throw new WorkflowAssistantErrorException(text)
                    },
                    cancellationToken);

                foreach (var message in _chatMessages
                             .Where(static candidate => candidate.Role == AppChatRole.Assistant)
                             .Where(static candidate => !string.IsNullOrWhiteSpace(candidate.Content))
                             .Where(message => completedMessages.All(completed => completed.Message.Id != message.Id)))
                {
                    _speakerIdsByMessageId.TryGetValue(message.Id, out var speakerId);
                    completedMessages.Add(new OrchestrationCompletedAssistantMessage(
                        (AppChatMessage)message,
                        speakerId ?? message.AgentId));
                }

                foreach (var completedMessage in completedMessages)
                {
                    await PublishCompletedMessageAsync(
                        completedMessage.Message,
                        completedMessage.SpeakerId,
                        writer,
                        _emittedCompletedMessageIds,
                        _emittedCompletedMessageContents,
                        cancellationToken);
                }

                var final = await ResolveFinalMessageAsync(
                    workflowRequest,
                    TaskSessionId,
                    completedMessages,
                    taskSessionStore,
                    cancellationToken);
                if (final is null)
                {
                    throw new WorkflowProducedNoResultException();
                }

                await writer.WriteAsync(new AgentRunCompleted(final), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WorkflowAssistantErrorException)
            {
                throw;
            }
            catch (WorkflowProducedNoResultException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                "Workflow execution failed. WorkflowId={WorkflowId}, WorkflowName={WorkflowName}, WorkflowKind={WorkflowKind}, ParticipantCount={ParticipantCount}",
                    workflowRequest.Workflow.Id,
                    workflowRequest.Workflow.DisplayName,
                    workflowRequest.Workflow.Kind,
                    workflowRequest.Workflow.Participants.Count);
                throw;
            }
            finally
            {
                writer.TryComplete();
            }
        }
    }

    private static async Task<bool> IsWorkflowExecutionCompleteAsync(
        AgentWorkflowExecutionDefinition execution,
        string taskSessionId,
        TaskSessionStore taskSessionStore,
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

    internal static async Task<AgentRunResult?> ResolveFinalMessageAsync(
        OrchestrationWorkflowSessionStartRequest request,
        string taskSessionId,
        IReadOnlyList<OrchestrationCompletedAssistantMessage> messages,
        TaskSessionStore taskSessionStore,
        CancellationToken cancellationToken)
    {
        var nonEmptyMessages = messages
            .Where(static message => !string.IsNullOrWhiteSpace(message.Message.Content))
            .DistinctBy(static message => message.Message.Content.Trim())
            .ToList();
        if (nonEmptyMessages.Count == 0)
        {
            return null;
        }

        var finalMessage = request.Workflow switch
        {
            SequentialWorkflowDefinition sequential => ResolveSequentialWorkflowFinal(sequential, nonEmptyMessages),
            ConcurrentWorkflowDefinition concurrent => ResolveConcurrentFinal(request, concurrent, nonEmptyMessages),
            GroupChatWorkflowDefinition => await ResolveGroupChatFinalAsync(
                request,
                taskSessionId,
                nonEmptyMessages,
                taskSessionStore,
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
            ["workflowKind"] = request.Workflow.Kind
        };

        if (!string.IsNullOrWhiteSpace(finalMessage.SpeakerId))
        {
            metadata["finalParticipantId"] = finalMessage.SpeakerId;
        }

        if (!string.IsNullOrWhiteSpace(finalMessage.Message.AgentName))
        {
            metadata["finalParticipantName"] = finalMessage.Message.AgentName;
        }

        metadata["finalMessageKind"] = finalMessage.SpeakerId == request.Workflow.Id
            ? "synthesized"
            : "participant";

        return new AgentRunResult
        {
            FinalMessageId = finalMessage.Message.Id.ToString("N"),
            FinalMessage = new AgentOutputMessage(
                request.SessionTitle ?? request.Workflow.DisplayName,
                finalMessage.Message.Content,
                string.IsNullOrWhiteSpace(finalMessage.SpeakerId) ? null : finalMessage.SpeakerId),
            Messages = nonEmptyMessages
                .Select(static message => new AgentOutputMessage(
                    string.IsNullOrWhiteSpace(message.Message.AgentName) ? "assistant" : message.Message.AgentName,
                    message.Message.Content,
                    string.IsNullOrWhiteSpace(message.SpeakerId) ? null : message.SpeakerId))
                .ToList(),
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

    [Obsolete]
    private static OrchestrationCompletedAssistantMessage? ResolveConcurrentFinal(
        OrchestrationWorkflowSessionStartRequest request,
        ConcurrentWorkflowDefinition workflow,
        IReadOnlyList<OrchestrationCompletedAssistantMessage> messages)
    {
        var orderedMessages = OrderConcurrentMessages(workflow, messages);
        if (workflow.Aggregation.Kind == ConcurrentWorkflowAggregationKind.ConcatenateAllMessages)
        {
            return new OrchestrationCompletedAssistantMessage(
                new AppChatMessage(
                    string.Join(Environment.NewLine + Environment.NewLine, orderedMessages.Select(static message => message.Message.Content)),
                    DateTime.Now,
                    AppChatRole.Assistant,
                    agentName: request.SessionTitle ?? request.Workflow.DisplayName)
                {
                    Id = Guid.Parse(CreateSyntheticMessageId())
                },
                request.Workflow.Id);
        }

        var sections = new List<string>();
        foreach (var participantId in workflow.ParticipantAgentIds)
        {
            var message = messages.LastOrDefault(candidate => BelongsTo(candidate, participantId));
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
                    agentName: request.SessionTitle ?? request.Workflow.DisplayName)
                {
                    Id = Guid.Parse(CreateSyntheticMessageId())
                },
                request.Workflow.Id);
    }

    private static async Task<OrchestrationCompletedAssistantMessage?> ResolveGroupChatFinalAsync(
        OrchestrationWorkflowSessionStartRequest request,
        string taskSessionId,
        IReadOnlyList<OrchestrationCompletedAssistantMessage> messages,
        TaskSessionStore taskSessionStore,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Workflow.Execution.CompletionSummaryLabel))
        {
            var snapshot = await taskSessionStore.TryGetSummaryAsync(
                taskSessionId,
                request.Workflow.Execution.CompletionSummaryLabel,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(snapshot?.Markdown))
            {
                return new OrchestrationCompletedAssistantMessage(
                    new AppChatMessage(
                        snapshot.Markdown,
                        DateTime.Now,
                        AppChatRole.Assistant,
                        agentName: request.SessionTitle ?? request.Workflow.DisplayName)
                    {
                        Id = Guid.Parse(CreateSyntheticMessageId())
                    },
                    request.Workflow.Id);
            }
        }

        return messages.LastOrDefault();
    }

    [Obsolete]
    private static IReadOnlyList<OrchestrationCompletedAssistantMessage> OrderConcurrentMessages(
        ConcurrentWorkflowDefinition workflow,
        IReadOnlyList<OrchestrationCompletedAssistantMessage> messages)
    {
        var ordered = new List<OrchestrationCompletedAssistantMessage>();
        var included = new HashSet<OrchestrationCompletedAssistantMessage>();

        foreach (var participantId in workflow.ParticipantAgentIds)
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

    private static bool BelongsTo(
        OrchestrationCompletedAssistantMessage message,
        string participantId) =>
        string.Equals(message.SpeakerId, participantId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(message.Message.AgentId, participantId, StringComparison.OrdinalIgnoreCase);

    private static async Task NotifyMessageAsync(
        IAppChatMessage message,
        bool isFinal,
        ChannelWriter<AgentRunEvent> writer,
        Dictionary<Guid, int> streamContentLengths,
        HashSet<Guid> emittedCompletedMessageIds,
        HashSet<string> emittedCompletedMessageContents,
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
            message.AgentId,
            writer,
            emittedCompletedMessageIds,
            emittedCompletedMessageContents,
            cancellationToken);
    }

    private static async Task PublishCompletedMessageAsync(
        IAppChatMessage message,
        string? participantId,
        ChannelWriter<AgentRunEvent> writer,
        HashSet<Guid> emittedCompletedMessageIds,
        HashSet<string> emittedCompletedMessageContents,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.Content) ||
            !emittedCompletedMessageIds.Add(message.Id) ||
            !emittedCompletedMessageContents.Add(message.Content.Trim()))
        {
            return;
        }

        await writer.WriteAsync(
            new AgentMessageCompleted(
                message.Id.ToString("N"),
                new AgentOutputMessage(
                    string.IsNullOrWhiteSpace(message.AgentName) ? "assistant" : message.AgentName,
                    message.Content,
                    participantId ?? message.AgentId)),
            cancellationToken);
    }

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

    private static string CreateSyntheticMessageId() =>
        Guid.NewGuid().ToString("N");
}

public sealed class WorkflowAssistantErrorException(string message) : Exception(message);

public sealed class WorkflowProducedNoResultException : Exception;
