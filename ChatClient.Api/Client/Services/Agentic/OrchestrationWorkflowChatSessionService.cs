using System.Collections.ObjectModel;
using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
#pragma warning disable MAAI001
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
#pragma warning restore MAAI001

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class OrchestrationWorkflowChatSessionService(
    TaskSessionStore taskSessionStore,
    OrchestrationWorkflowSessionBootstrapper sessionBootstrapper,
    OrchestrationWorkflowTurnCoordinator turnCoordinator,
    OrchestrationWorkflowPassExecutor passExecutor,
    OrchestrationWorkflowEventStreamProcessor eventStreamProcessor,
    IChatEngineStreamingBridge streamingBridge) : IOrchestrationWorkflowSessionService
{
    private readonly AppChat _chat = new();
    private readonly Dictionary<Guid, StreamingAppChatMessage> _activeStreams = [];
    private readonly Dictionary<Guid, string?> _activeSpeakerIdsByStreamId = [];
    private readonly Dictionary<string, AIAgent> _workflowAgentsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _agentIdsByExecutorId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _agentIdsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _agentNamesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, string?> _speakerIdsByMessageId = [];
    private readonly List<string> _assistantSpeakerIds = [];
    private CancellationTokenSource? _cancellationTokenSource;
    private OrchestrationWorkflowSessionStartRequest? _parameters;

    public event Action<bool>? AnsweringStateChanged;
    public event Action? ChatReset;
    public event Func<IAppChatMessage, Task>? MessageAdded;
    public event Func<IAppChatMessage, bool, Task>? MessageUpdated;
    public event Func<Guid, Task>? MessageDeleted;

    public bool IsAnswering { get; private set; }

    public Guid Id => _chat.Id;

    public string? TaskSessionId { get; private set; }

    public IReadOnlyCollection<AgentExecutionSpec> Agents => _chat.Agents;

    public ObservableCollection<IAppChatMessage> Messages => _chat.Messages;
    IReadOnlyCollection<IAppChatMessage> IChatSessionService.Messages => _chat.Messages;

    public async Task StartAsync(
        OrchestrationWorkflowSessionStartRequest request,
        CancellationToken cancellationToken = default)
    {
        var bootstrapResult = await sessionBootstrapper.BootstrapAsync(request, cancellationToken);
        ApplyBootstrapResult(bootstrapResult);
    }

    public void ResetChat()
    {
        ClearChatState();
        ClearWorkflowIdentityState();
        _parameters = null;
        TaskSessionId = null;
        ChatReset?.Invoke();
    }

    public async Task CancelAsync()
    {
        _cancellationTokenSource?.Cancel();

        if (_activeStreams.Count > 0)
        {
            foreach (var stream in _activeStreams.Values.ToList())
            {
                var canceled = streamingBridge.Cancel(stream);
                ReplaceMessage(stream, canceled);
                await (MessageUpdated?.Invoke(canceled, true) ?? Task.CompletedTask);
            }

            _activeStreams.Clear();
            _activeSpeakerIdsByStreamId.Clear();
        }

        UpdateAnsweringState(false);
    }

    public Task SendAsync(
        string text,
        IReadOnlyList<AppChatMessageFile>? files = null,
        CancellationToken cancellationToken = default)
    {
        if (_parameters is null || string.IsNullOrWhiteSpace(TaskSessionId))
        {
            throw new InvalidOperationException("Workflow session not started.");
        }

        return GenerateAnswerAsync(text, files ?? [], cancellationToken);
    }

    public Task KickoffAsync(CancellationToken cancellationToken = default)
    {
        if (_parameters is null || string.IsNullOrWhiteSpace(TaskSessionId))
        {
            throw new InvalidOperationException("Workflow session not started.");
        }

        return ExecuteWorkflowTurnAsync(null, [], includeUserMessage: false, cancellationToken);
    }

    public ChatEngineSessionState GetState()
    {
        if (_parameters is null)
        {
            throw new InvalidOperationException("Workflow session not started.");
        }

        return new ChatEngineSessionState
        {
            Configuration = _parameters.Configuration,
            Agents = _chat.Agents.ToList(),
            Messages = _chat.Messages.ToList(),
            ChatStrategyName = $"AgentOrchestration:{_parameters.Workflow.Kind}"
        };
    }

    public async Task DeleteMessageAsync(Guid messageId)
    {
        if (IsAnswering)
        {
            return;
        }

        var message = _chat.Messages.FirstOrDefault(m => m.Id == messageId);
        if (message is null)
        {
            return;
        }

        _chat.Messages.Remove(message);
        if (message.Role == AppChatRole.Assistant)
        {
            _speakerIdsByMessageId.Remove(message.Id);
            RebuildAssistantSpeakerHistory();
        }

        await (MessageDeleted?.Invoke(messageId) ?? Task.CompletedTask);
    }

    private async Task GenerateAnswerAsync(
        string text,
        IReadOnlyList<AppChatMessageFile> files,
        CancellationToken cancellationToken)
    {
        await ExecuteWorkflowTurnAsync(text, files, includeUserMessage: true, cancellationToken);
    }

    private async Task ExecuteWorkflowTurnAsync(
        string? text,
        IReadOnlyList<AppChatMessageFile> files,
        bool includeUserMessage,
        CancellationToken cancellationToken)
    {
        if (IsAnswering || _parameters is null || string.IsNullOrWhiteSpace(TaskSessionId))
        {
            return;
        }

        if (includeUserMessage)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var userMessage = new AppChatMessage(text, DateTime.Now, AppChatRole.User, files: files);
            await AddMessageAsync(userMessage);
            await taskSessionStore.AppendTurnAsync(
                TaskSessionId,
                "user",
                OrchestrationWorkflowConversationBuilder.BuildUserMessage(text, files),
                "user",
                cancellationToken);
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        UpdateAnsweringState(true);

        try
        {
            var parameters = _parameters;
            await turnCoordinator.RunAsync(
                new OrchestrationWorkflowTurnExecutionRequest
                {
                    WorkflowDisplayName = parameters.Workflow.DisplayName,
                    Execution = parameters.Workflow.Execution,
                    IsExecutionCompleteAsync = cancellation => IsWorkflowExecutionCompleteAsync(
                        parameters.Workflow.Execution,
                        cancellation),
                    ExecutePassAsync = cancellation => ExecuteWorkflowPassAsync(parameters, cancellation),
                    ProcessCompletedAssistantMessagesAsync = ProcessCompletedAssistantMessagesAsync,
                    HandleAssistantErrorAsync = HandleError
                },
                _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            UpdateAnsweringState(false);
        }
    }

    private async Task<OrchestrationWorkflowPassResult> ExecuteWorkflowPassAsync(
        OrchestrationWorkflowSessionStartRequest parameters,
        CancellationToken cancellationToken)
    {
        var currentMessages = _chat.Messages.ToList();
        var eventStreamContext = CreateEventStreamContext(
            parameters.Configuration.ModelName,
            parameters.Workflow,
            currentMessages);
        return await passExecutor.ExecuteAsync(
            new OrchestrationWorkflowPassExecutionRequest
            {
                Workflow = parameters.Workflow,
                SessionId = TaskSessionId ?? Guid.NewGuid().ToString("N"),
                Messages = currentMessages,
                AssistantSpeakerIds = _assistantSpeakerIds.ToList(),
                RuntimeAgentsById = _workflowAgentsById,
                EventStreamContext = eventStreamContext
            },
            cancellationToken);
    }

    internal Task<bool> DrainWorkflowEventsAsync(
        IAsyncEnumerable<WorkflowEvent> workflowEvents,
        string modelName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflowEvents);

        var eventStreamContext = CreateEventStreamContext(modelName);
        return eventStreamProcessor.DrainAsync(
            workflowEvents,
            eventStreamContext,
            new OrchestrationWorkflowPassStreamingState(_assistantSpeakerIds.Count),
            eventStreamProcessor.CreateDeliveredAssistantMessagesSnapshot(eventStreamContext),
            [],
            cancellationToken);
    }

    private async Task ProcessCompletedAssistantMessagesAsync(
        IReadOnlyList<OrchestrationCompletedAssistantMessage> completedAssistantMessages,
        CancellationToken cancellationToken)
    {
        foreach (var completedMessage in completedAssistantMessages)
        {
            await taskSessionStore.AppendTurnAsync(
                TaskSessionId,
                "assistant",
                completedMessage.Message.Content,
                completedMessage.SpeakerId,
                cancellationToken);

            _speakerIdsByMessageId[completedMessage.Message.Id] = completedMessage.SpeakerId;
            if (!string.IsNullOrWhiteSpace(completedMessage.SpeakerId))
            {
                _assistantSpeakerIds.Add(completedMessage.SpeakerId);
            }
        }
    }

    private OrchestrationWorkflowEventStreamContext CreateEventStreamContext(
        string modelName,
        IOrchestrationWorkflowDefinition? workflow = null,
        IReadOnlyList<IAppChatMessage>? messages = null)
    {
        return new OrchestrationWorkflowEventStreamContext
        {
            ModelName = modelName,
            Workflow = workflow ?? _parameters?.Workflow,
            Messages = messages ?? _chat.Messages.ToList(),
            SpeakerIdsByMessageId = _speakerIdsByMessageId,
            ActiveStreams = _activeStreams,
            ActiveSpeakerIdsByStreamId = _activeSpeakerIdsByStreamId,
            AgentIdsByExecutorId = _agentIdsByExecutorId,
            AgentIdsByName = _agentIdsByName,
            AgentNamesById = _agentNamesById,
            AddMessageAsync = AddMessageAsync,
            ReplaceMessage = ReplaceMessage,
            NotifyMessageUpdatedAsync = (message, isFinal) =>
                MessageUpdated?.Invoke(message, isFinal) ?? Task.CompletedTask
        };
    }

    private void ApplyBootstrapResult(OrchestrationWorkflowSessionBootstrapResult bootstrapResult)
    {
        ClearWorkflowIdentityState();
        foreach (var runtimeAgent in bootstrapResult.RuntimeAgents)
        {
            _workflowAgentsById[runtimeAgent.AgentId] = runtimeAgent.RuntimeAgent;
            RegisterAgentIdentity(
                runtimeAgent.AgentId,
                runtimeAgent.AgentName,
                runtimeAgent.ExecutorId);
        }

        _parameters = bootstrapResult.Request;
        TaskSessionId = bootstrapResult.TaskSessionId;

        ClearChatState();
        _chat.SetAgents(bootstrapResult.Request.Agents.Select(static agent => agent.Agent.Clone()));
        ChatReset?.Invoke();
    }

    private void ClearChatState()
    {
        _chat.Reset();
        _activeStreams.Clear();
        _activeSpeakerIdsByStreamId.Clear();
        _speakerIdsByMessageId.Clear();
        _assistantSpeakerIds.Clear();
    }

    private void ClearWorkflowIdentityState()
    {
        _workflowAgentsById.Clear();
        _agentIdsByExecutorId.Clear();
        _agentIdsByName.Clear();
        _agentNamesById.Clear();
    }

    private async Task<bool> IsWorkflowExecutionCompleteAsync(
        AgentWorkflowExecutionDefinition execution,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(TaskSessionId))
        {
            return false;
        }

        var snapshot = await taskSessionStore.GetSessionAsync(TaskSessionId, cancellationToken);

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

    internal void RegisterAgentIdentity(
        string agentId,
        string agentName,
        string? executorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        _agentIdsByExecutorId[agentId] = agentId;
        if (!string.IsNullOrWhiteSpace(executorId))
        {
            _agentIdsByExecutorId[executorId] = agentId;
        }

        _agentIdsByExecutorId[agentName] = agentId;
        _agentIdsByName[agentName] = agentId;
        _agentNamesById[agentId] = agentName;
    }

    private async Task AddMessageAsync(IAppChatMessage message)
    {
        if (_chat.Messages.Any(m => m.Id == message.Id))
        {
            return;
        }

        _chat.Messages.Add(message);
        await (MessageAdded?.Invoke(message) ?? Task.CompletedTask);
    }

    private void ReplaceMessage(IAppChatMessage source, IAppChatMessage replacement)
    {
        int index = _chat.Messages.IndexOf(source);
        if (index >= 0)
        {
            _chat.Messages[index] = replacement;
        }
        else
        {
            _chat.Messages.Add(replacement);
        }
    }

    private void RebuildAssistantSpeakerHistory()
    {
        _assistantSpeakerIds.Clear();

        foreach (var message in _chat.Messages)
        {
            if (message.IsStreaming || message.Role != AppChatRole.Assistant)
            {
                continue;
            }

            if (_speakerIdsByMessageId.TryGetValue(message.Id, out var speakerId) &&
                !string.IsNullOrWhiteSpace(speakerId))
            {
                _assistantSpeakerIds.Add(speakerId);
            }
        }
    }

    private async Task HandleError(string text)
    {
        await AddMessageAsync(new AppChatMessage(text, DateTime.Now, AppChatRole.Assistant));
    }

    private void UpdateAnsweringState(bool isAnswering)
    {
        IsAnswering = isAnswering;
        AnsweringStateChanged?.Invoke(isAnswering);
    }
}
