using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Runtime;
using ChatClient.Api.Client.Services.Agentic;
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
    IWorkflowSessionState executionState,
    IWorkflowResultResolver resultResolver,
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
                Configuration = request.Configuration,
                CreationContext = request.CreationContext,
                ParentRunContext = request.ParentRunContext,
                SessionTitle = request.SessionTitle,
                SessionDescription = request.SessionDescription,
                StartInputs = request.StartInputs
            },
            cancellationToken);

        var session = new WorkflowExecutionSession(
            bootstrap,
            turnCoordinator,
            passExecutor,
            executionState,
            resultResolver,
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
        IWorkflowSessionState executionState,
        IWorkflowResultResolver resultResolver,
        ILogger logger)
    {
        private readonly WorkflowExecutionContext _context = new();
        public string SessionId => bootstrap.SessionId;

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
                    _context.RegisterParticipant(
                        runtimeAgent.AgentId,
                        runtimeAgent.AgentName,
                        runtimeAgent.ExecutorId);
                }

                if (!string.IsNullOrWhiteSpace(request.UserMessage))
                {
                    var userChatMessage = new AppChatMessage(
                        request.UserMessage,
                        DateTime.Now,
                        AppChatRole.User);
                    await AddMessageAsync(userChatMessage, _context.Messages);
                }

                await turnCoordinator.RunAsync(
                    new OrchestrationWorkflowTurnExecutionRequest
                    {
                        WorkflowDisplayName = workflowRequest.Workflow.DisplayName,
                        Execution = workflowRequest.Workflow.Execution,
                        IsExecutionCompleteAsync = cancellation => executionState.IsCompletedAsync(
                            SessionId,
                            workflowRequest.Workflow.Execution,
                            cancellation),
                        ExecutePassAsync = cancellation => passExecutor.ExecuteAsync(
                            new OrchestrationWorkflowPassExecutionRequest
                            {
                                Workflow = workflowRequest.Workflow,
                                SessionId = SessionId,
                                Messages = _context.Messages.ToList(),
                                AssistantSpeakerIds = _context.AssistantSpeakerIds.ToList(),
                                RuntimeAgentsById = bootstrap.RuntimeAgents.ToDictionary(
                                    static agent => agent.AgentId,
                                    static agent => agent.RuntimeAgent,
                                    StringComparer.OrdinalIgnoreCase),
                                EventStreamContext = new OrchestrationWorkflowEventStreamContext
                                {
                                    ModelName = workflowRequest.Configuration.ModelName,
                                    Workflow = workflowRequest.Workflow,
                                    Messages = _context.Messages.ToList(),
                                    SpeakerIdsByMessageId = _context.SpeakerIdsByMessageId,
                                    ActiveStreams = _context.ActiveStreams,
                                    ActiveSpeakerIdsByStreamId = _context.ActiveSpeakerIdsByStreamId,
                                    AgentIdsByExecutorId = _context.ParticipantIdsByEventSource,
                                    AgentNamesById = _context.ParticipantNamesById,
                                    AddMessageAsync = message => AddMessageAsync(message, _context.Messages),
                                    ReplaceMessage = (source, replacement) => ReplaceMessage(
                                        source,
                                        replacement,
                                        _context.Messages),
                                    NotifyMessageUpdatedAsync = (message, isFinal) => NotifyMessageAsync(
                                        message,
                                        isFinal,
                                        writer,
                                        _context.StreamContentLengths,
                                        _context.EmittedCompletedMessageIds,
                                        cancellation)
                                }
                            },
                            cancellation),
                        ProcessCompletedAssistantMessagesAsync = async (messages, cancellation) =>
                        {
                            foreach (var completedMessage in messages)
                            {
                                completedMessages.Add(completedMessage);
                                _context.SpeakerIdsByMessageId[completedMessage.Message.Id] = completedMessage.SpeakerId;
                                if (!string.IsNullOrWhiteSpace(completedMessage.SpeakerId))
                                {
                                    _context.AssistantSpeakerIds.Add(completedMessage.SpeakerId);
                                }
                            }
                        },
                        HandleAssistantErrorAsync = text => throw new WorkflowAssistantErrorException(text)
                    },
                    cancellationToken);

                foreach (var message in _context.Messages
                             .Where(static candidate => candidate.Role == AppChatRole.Assistant)
                             .Where(static candidate => !string.IsNullOrWhiteSpace(candidate.Content))
                             .Where(message => completedMessages.All(completed => completed.Message.Id != message.Id)))
                {
                    _context.SpeakerIdsByMessageId.TryGetValue(message.Id, out var speakerId);
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
                        _context.EmittedCompletedMessageIds,
                        cancellationToken);
                }

                var final = await resultResolver.ResolveAsync(
                    new WorkflowResultResolutionContext(
                        workflowRequest,
                        SessionId,
                        completedMessages),
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
            message.AgentId,
            writer,
            emittedCompletedMessageIds,
            cancellationToken);
    }

    private static async Task PublishCompletedMessageAsync(
        IAppChatMessage message,
        string? participantId,
        ChannelWriter<AgentRunEvent> writer,
        HashSet<Guid> emittedCompletedMessageIds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.Content) ||
            !emittedCompletedMessageIds.Add(message.Id))
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

}

public sealed class WorkflowAssistantErrorException(string message) : Exception(message);

public sealed class WorkflowProducedNoResultException : Exception;
