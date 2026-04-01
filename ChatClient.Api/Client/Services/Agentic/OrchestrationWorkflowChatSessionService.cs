using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.Runtime;
using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
#pragma warning disable MAAI001
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
#pragma warning restore MAAI001

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class OrchestrationWorkflowChatSessionService(
    ILogger<OrchestrationWorkflowChatSessionService> logger,
    IModelCapabilityService modelCapabilityService,
    TaskSessionStore taskSessionStore,
    MarkdownDocumentIntakeService documentIntakeService,
    AgenticRuntimeAgentFactory runtimeAgentFactory,
    IEnumerable<IOrchestrationRuntimeWorkflowBuilder> runtimeWorkflowBuilders,
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
    private readonly IReadOnlyList<IOrchestrationRuntimeWorkflowBuilder> _runtimeWorkflowBuilders =
        runtimeWorkflowBuilders.ToArray();
    private static readonly Lazy<MethodInfo?> GetDescriptiveIdMethod = new(static () =>
        Type.GetType(
                "Microsoft.Agents.AI.Workflows.AIAgentExtensions, Microsoft.Agents.AI.Workflows",
                throwOnError: false)?
            .GetMethod(
                "GetDescriptiveId",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: [typeof(AIAgent)],
                modifiers: null));

    public event Action<bool>? AnsweringStateChanged;
    public event Action? ChatReset;
    public event Func<IAppChatMessage, Task>? MessageAdded;
    public event Func<IAppChatMessage, bool, Task>? MessageUpdated;
    public event Func<Guid, Task>? MessageDeleted;

    public bool IsAnswering { get; private set; }

    public Guid Id => _chat.Id;

    public string? TaskSessionId { get; private set; }

    public IReadOnlyCollection<AgentDescription> AgentDescriptions => _chat.AgentDescriptions;

    public ObservableCollection<IAppChatMessage> Messages => _chat.Messages;
    IReadOnlyCollection<IAppChatMessage> IChatEngineSessionService.Messages => _chat.Messages;

    public async Task StartAsync(
        OrchestrationWorkflowSessionStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Agents.Count == 0)
        {
            throw new ArgumentException("At least one workflow agent must be provided.", nameof(request));
        }

        var stage = "validate-agents";

        try
        {
            await ValidateResolvedAgentsAsync(request.Agents, cancellationToken);

            stage = "validate-workflow";
            ValidateWorkflowDefinition(request.Workflow, request.Agents);
            var normalizedStartInputs = NormalizeStartInputs(request.Workflow, request.StartInputs);

            stage = "create-task-session";
            var session = await taskSessionStore.CreateSessionAsync(
                request.SessionTitle,
                request.SessionDescription,
                cancellationToken);
            TaskSessionId = session.SessionId;

            stage = "set-initial-phase";
            await taskSessionStore.SetPhaseAsync(TaskSessionId, "intake", cancellationToken);

            foreach (var startInput in normalizedStartInputs)
            {
                var definition = request.Workflow.StartInputs.First(input =>
                    string.Equals(input.Key, startInput.Key, StringComparison.OrdinalIgnoreCase));

                if (definition.Kind == WorkflowStartInputKind.MarkdownDocument)
                {
                    stage = $"prepare-document:{definition.Key}";
                    var prepared = await PrepareDocumentAsync(definition, startInput, cancellationToken);
                    if (prepared is null)
                    {
                        continue;
                    }

                    stage = $"attach-document:{definition.Key}";
                    await taskSessionStore.AttachDocumentAsync(
                        TaskSessionId,
                        definition.Key,
                        prepared.Markdown,
                        prepared.Title,
                        prepared.SourceFile,
                        cancellationToken);
                    continue;
                }

                stage = $"attach-parameter:{definition.Key}";
                await taskSessionStore.SetParameterAsync(
                    TaskSessionId,
                    definition.Key,
                    MapParameterValueKind(definition.Kind),
                    NormalizeParameterValue(definition, startInput),
                    cancellationToken);
            }

            stage = "bind-task-session";
            var sessionBoundAgents = request.Agents
                .Select(agent => BindTaskSession(agent, TaskSessionId))
                .ToList();

            _workflowAgentsById.Clear();
            _agentIdsByExecutorId.Clear();
            _agentIdsByName.Clear();
            _agentNamesById.Clear();
            _speakerIdsByMessageId.Clear();
            _assistantSpeakerIds.Clear();

            foreach (var sessionBoundAgent in sessionBoundAgents)
            {
                stage = $"build-runtime-agent:{sessionBoundAgent.Agent.AgentId}";
                var runtimeRequest = sessionBoundAgent.Agent
                    .ForRun()
                    .UsingModel(sessionBoundAgent.Model)
                    .WithConfiguration(request.Configuration)
                    .WithConversation([])
                    .WithUserMessage(string.Empty)
                    .Build();
                var builtAgent = await runtimeAgentFactory.CreateAsync(
                    runtimeRequest,
                    requireFunctionCalling: true,
                    cancellationToken: cancellationToken);

                _workflowAgentsById[sessionBoundAgent.Agent.AgentId] = builtAgent.Agent;
                _agentIdsByExecutorId[sessionBoundAgent.Agent.AgentId] = sessionBoundAgent.Agent.AgentId;
                var executorId = TryGetAgentExecutorId(builtAgent.Agent);
                if (!string.IsNullOrWhiteSpace(executorId))
                {
                    _agentIdsByExecutorId[executorId] = sessionBoundAgent.Agent.AgentId;
                }

                if (!string.IsNullOrWhiteSpace(sessionBoundAgent.Agent.AgentName))
                {
                    _agentIdsByExecutorId[sessionBoundAgent.Agent.AgentName] = sessionBoundAgent.Agent.AgentId;
                }

                _agentIdsByName[sessionBoundAgent.Agent.AgentName] = sessionBoundAgent.Agent.AgentId;
                _agentNamesById[sessionBoundAgent.Agent.AgentId] = sessionBoundAgent.Agent.AgentName;
            }

            stage = "store-parameters";
            _parameters = new OrchestrationWorkflowSessionStartRequest
            {
                Workflow = request.Workflow,
                Agents = sessionBoundAgents,
                Configuration = request.Configuration,
                SessionTitle = request.SessionTitle,
                SessionDescription = request.SessionDescription,
                StartInputs = normalizedStartInputs
            };

            stage = "reset-chat";
            _chat.Reset();
            _activeStreams.Clear();
            _activeSpeakerIdsByStreamId.Clear();
            _speakerIdsByMessageId.Clear();
            _assistantSpeakerIds.Clear();
            _chat.SetAgents(sessionBoundAgents.Select(CreateRuntimeAgentDescription));
            ChatReset?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to start orchestration workflow session at stage {Stage}. WorkflowId={WorkflowId}, AgentCount={AgentCount}, TaskSessionId={TaskSessionId}",
                stage,
                request.Workflow.Id,
                request.Agents.Count,
                TaskSessionId);
            throw;
        }
    }

    async Task IChatEngineSessionService.StartAsync(
        ChatEngineSessionStartRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        throw new InvalidOperationException(
            "This session service is workflow-specific. Use StartAsync(OrchestrationWorkflowSessionStartRequest).");
    }

    public void ResetChat()
    {
        _chat.Reset();
        _activeStreams.Clear();
        _activeSpeakerIdsByStreamId.Clear();
        _workflowAgentsById.Clear();
        _agentIdsByExecutorId.Clear();
        _agentIdsByName.Clear();
        _agentNamesById.Clear();
        _speakerIdsByMessageId.Clear();
        _assistantSpeakerIds.Clear();
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
            Agents = _chat.AgentDescriptions.ToList(),
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
        if (message.Role == ChatRole.Assistant)
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

            var userMessage = new AppChatMessage(text, DateTime.Now, ChatRole.User, files: files);
            await AddMessageAsync(userMessage);
            await taskSessionStore.AppendTurnAsync(
                TaskSessionId,
                "user",
                BuildUserMessage(text, files),
                "user",
                cancellationToken);
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        UpdateAnsweringState(true);

        try
        {
            await RunWorkflowTurnAsync(_parameters, _cancellationTokenSource.Token);
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

    private async Task RunWorkflowTurnAsync(
        OrchestrationWorkflowSessionStartRequest parameters,
        CancellationToken cancellationToken)
    {
        var execution = parameters.Workflow.Execution;
        var automaticAssistantTurnsUsed = 0;
        var passNumber = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (execution.Mode == AgentWorkflowExecutionMode.Autonomous &&
                await IsWorkflowExecutionCompleteAsync(execution, cancellationToken))
            {
                return;
            }

            if (execution.Mode == AgentWorkflowExecutionMode.Interactive && passNumber > 0)
            {
                return;
            }

            if (execution.Mode == AgentWorkflowExecutionMode.Autonomous &&
                automaticAssistantTurnsUsed >= execution.MaxAutomaticTurns)
            {
                throw new InvalidOperationException(
                    $"Autonomous workflow '{parameters.Workflow.DisplayName}' reached its automatic turn limit ({execution.MaxAutomaticTurns}) before completion.");
            }

            passNumber++;
            var passResult = await ExecuteWorkflowPassAsync(parameters, cancellationToken);

            if (passResult.CompletedAssistantMessages.Count == 0)
            {
                if (execution.Mode == AgentWorkflowExecutionMode.Autonomous &&
                    await IsWorkflowExecutionCompleteAsync(execution, cancellationToken))
                {
                    return;
                }

                await HandleError("Workflow returned an empty response.");
                return;
            }

            await ProcessCompletedAssistantMessagesAsync(
                passResult.CompletedAssistantMessages,
                cancellationToken);

            automaticAssistantTurnsUsed += passResult.CompletedAssistantMessages.Count;

            if (execution.Mode == AgentWorkflowExecutionMode.Autonomous &&
                automaticAssistantTurnsUsed > execution.MaxAutomaticTurns)
            {
                throw new InvalidOperationException(
                    $"Autonomous workflow '{parameters.Workflow.DisplayName}' exceeded its automatic turn limit ({execution.MaxAutomaticTurns}).");
            }

            if (execution.Mode != AgentWorkflowExecutionMode.Autonomous)
            {
                return;
            }

            if (await IsWorkflowExecutionCompleteAsync(execution, cancellationToken))
            {
                return;
            }

            if (automaticAssistantTurnsUsed >= execution.MaxAutomaticTurns)
            {
                throw new InvalidOperationException(
                    $"Autonomous workflow '{parameters.Workflow.DisplayName}' reached its automatic turn limit ({execution.MaxAutomaticTurns}) before completion.");
            }
        }
    }

    private async Task<WorkflowPassResult> ExecuteWorkflowPassAsync(
        OrchestrationWorkflowSessionStartRequest parameters,
        CancellationToken cancellationToken)
    {
        var workflow = BuildWorkflow(parameters.Workflow);
        var conversation = BuildConversation(_chat.Messages);
        var sessionId = TaskSessionId ?? Guid.NewGuid().ToString("N");

        // Microsoft.Agents.AI.Workflows 1.0.0-rc4 drives chat-protocol runs by enqueueing the
        // initial conversation and an implicit TurnToken back-to-back inside RunAsync(). With the
        // rc4 input waiter this can overflow its binary semaphore. Drive the same protocol in
        // two explicit batches instead so each signal is consumed before the next one is sent.
        await using var run = await InProcessExecution.OpenStreamingAsync(
            workflow,
            sessionId,
            cancellationToken);
        var completedAssistantMessages = new List<CompletedWorkflowAssistantMessage>();
        var streamingState = new WorkflowPassStreamingState(_assistantSpeakerIds.Count);
        var assistantOutputObserved = false;

        if (conversation.Count > 0)
        {
            assistantOutputObserved |= await ExecuteWorkflowBatchAsync(
                run,
                conversation,
                parameters.Configuration.ModelName,
                streamingState,
                completedAssistantMessages,
                cancellationToken);
        }

        assistantOutputObserved |= await ExecuteWorkflowBatchAsync(
            run,
            new TurnToken(emitEvents: true),
            parameters.Configuration.ModelName,
            streamingState,
            completedAssistantMessages,
            cancellationToken);

        var status = await run.GetStatusAsync(cancellationToken);
        logger.LogDebug(
            "Workflow pass completed. Status={Status}, AssistantOutputObserved={AssistantOutputObserved}, CompletedAssistantMessages={CompletedAssistantMessages}",
            status,
            assistantOutputObserved,
            completedAssistantMessages.Count);

        switch (status)
        {
            case RunStatus.Ended:
            case RunStatus.Idle:
                break;

            case RunStatus.PendingRequests:
                throw new InvalidOperationException(
                    "Workflow requested unsupported external input.");

            case RunStatus.NotStarted:
            case RunStatus.Running:
            default:
                throw new InvalidOperationException(
                    $"Workflow returned unexpected run status '{status}'.");
        }

        foreach (var stream in _activeStreams.Values.ToList())
        {
            await FinalizeStreamAsync(
                stream,
                parameters.Configuration.ModelName,
                completedAssistantMessages);
        }

        _activeStreams.Clear();
        _activeSpeakerIdsByStreamId.Clear();

        return new WorkflowPassResult(completedAssistantMessages);
    }

    private async Task<bool> ProcessWorkflowEventsAsync(
        IEnumerable<WorkflowEvent> workflowEvents,
        string modelName,
        WorkflowPassStreamingState streamingState,
        List<CompletedWorkflowAssistantMessage> completedAssistantMessages,
        CancellationToken cancellationToken)
    {
        var assistantOutputObserved = false;

        foreach (var workflowEvent in workflowEvents)
        {
            switch (workflowEvent)
            {
                case AgentResponseUpdateEvent updateEvent:
                    var updateText = ExtractUpdateText(updateEvent);
                    if (string.IsNullOrWhiteSpace(updateText))
                    {
                        break;
                    }

                    var stream = await GetOrCreateStreamAsync(
                        updateEvent.ExecutorId,
                        updateText,
                        modelName,
                        streamingState,
                        completedAssistantMessages,
                        cancellationToken);

                    assistantOutputObserved = true;
                    streamingBridge.Append(stream, updateText);
                    await (MessageUpdated?.Invoke(stream, true) ?? Task.CompletedTask);
                    break;

                case WorkflowOutputEvent outputEvent:
                    foreach (var chatMessage in ExtractOutputMessages(outputEvent))
                    {
                        var outputText = chatMessage.Text;
                        if (string.IsNullOrWhiteSpace(outputText))
                        {
                            continue;
                        }

                        if (chatMessage.Role != ChatRole.Assistant)
                        {
                            continue;
                        }

                        assistantOutputObserved = true;
                        await PublishCompletedOutputMessageAsync(
                            chatMessage,
                            modelName,
                            completedAssistantMessages,
                            cancellationToken);
                    }
                    break;

                case ExecutorFailedEvent failedEvent:
                    throw failedEvent.Data ?? new InvalidOperationException(
                        $"Workflow executor '{failedEvent.ExecutorId}' failed without an exception payload.");

                case RequestInfoEvent requestInfoEvent:
                    throw new InvalidOperationException(
                        $"Workflow requested unsupported external input: {requestInfoEvent.Request}");

            }
        }

        return assistantOutputObserved;
    }

    private async Task<bool> ExecuteWorkflowBatchAsync<TInput>(
        StreamingRun run,
        TInput input,
        string modelName,
        WorkflowPassStreamingState streamingState,
        List<CompletedWorkflowAssistantMessage> completedAssistantMessages,
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

        var workflowEvents = await CollectWorkflowEventsAsync(run, cancellationToken);
        return await ProcessWorkflowEventsAsync(
            workflowEvents,
            modelName,
            streamingState,
            completedAssistantMessages,
            cancellationToken);
    }

    private static async Task<List<WorkflowEvent>> CollectWorkflowEventsAsync(
        StreamingRun run,
        CancellationToken cancellationToken)
    {
        List<WorkflowEvent> workflowEvents = [];

        await foreach (var workflowEvent in run.WatchStreamAsync(cancellationToken))
        {
            workflowEvents.Add(workflowEvent);

            if (workflowEvent is RequestInfoEvent)
            {
                break;
            }
        }

        return workflowEvents;
    }

    private async Task ProcessCompletedAssistantMessagesAsync(
        IReadOnlyList<CompletedWorkflowAssistantMessage> completedAssistantMessages,
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

    private Workflow BuildWorkflow(IOrchestrationWorkflowDefinition definition)
    {
        var builder = _runtimeWorkflowBuilders.FirstOrDefault(candidate => candidate.CanBuild(definition));
        if (builder is null)
        {
            throw new InvalidOperationException(
                $"Workflow kind '{definition.Kind}' does not have a registered runtime builder.");
        }

        return builder.Build(
            definition,
            _workflowAgentsById,
            new OrchestrationRuntimeBuildContext
            {
                AssistantSpeakerIds = _assistantSpeakerIds.ToList()
            });
    }

    private static IEnumerable<ChatMessage> ExtractOutputMessages(WorkflowOutputEvent outputEvent)
    {
        if (outputEvent.Is<List<ChatMessage>>(out var listMessages) && listMessages is not null)
        {
            return listMessages;
        }

        if (outputEvent.Is<IReadOnlyList<ChatMessage>>(out var readOnlyMessages) && readOnlyMessages is not null)
        {
            return readOnlyMessages;
        }

        if (outputEvent.Is<ChatMessage>(out var singleMessage) && singleMessage is not null)
        {
            return [singleMessage];
        }

        if (outputEvent.Is<string>(out var stringMessage) && !string.IsNullOrWhiteSpace(stringMessage))
        {
            return [new ChatMessage(ChatRole.Assistant, stringMessage)];
        }

        return [];
    }

    private static string? ExtractUpdateText(AgentResponseUpdateEvent updateEvent)
    {
        if (!string.IsNullOrWhiteSpace(updateEvent.Update.Text))
        {
            return updateEvent.Update.Text;
        }

        return updateEvent.Update.ToString();
    }

    private string? ResolveSpeakerName(string? speakerId)
    {
        if (string.IsNullOrWhiteSpace(speakerId))
        {
            return null;
        }

        return _agentNamesById.TryGetValue(speakerId, out var speakerName)
            ? speakerName
            : speakerId;
    }

    private string? ResolveSpeakerIdFromAuthorName(string? authorName)
    {
        if (string.IsNullOrWhiteSpace(authorName))
        {
            return null;
        }

        if (_agentIdsByName.TryGetValue(authorName, out var speakerId))
        {
            return speakerId;
        }

        return _agentIdsByExecutorId.TryGetValue(authorName, out speakerId)
            ? speakerId
            : null;
    }

    private async Task PublishCompletedOutputMessageAsync(
        ChatMessage chatMessage,
        string modelName,
        List<CompletedWorkflowAssistantMessage> completedAssistantMessages,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var outputText = chatMessage.Text;
        if (string.IsNullOrWhiteSpace(outputText))
        {
            return;
        }

        var speakerId = ResolveSpeakerIdFromAuthorName(chatMessage.AuthorName);
        var speakerName = ResolveSpeakerName(speakerId) ?? chatMessage.AuthorName;
        var existing = _activeStreams.Values.LastOrDefault();

        if (existing is not null)
        {
            _activeSpeakerIdsByStreamId.TryGetValue(existing.Id, out var existingSpeakerId);
            if (IsSameSpeaker(existingSpeakerId, existing.AgentName, speakerId, speakerName))
            {
                UpdateStreamSpeaker(existing, speakerId, speakerName);
                if (!string.IsNullOrWhiteSpace(speakerId))
                {
                    _activeSpeakerIdsByStreamId[existing.Id] = speakerId;
                }

                if (!string.Equals(existing.Content, outputText, StringComparison.Ordinal))
                {
                    existing.ResetContent();
                    existing.Append(outputText);
                }

                await FinalizeStreamAsync(existing, modelName, completedAssistantMessages);
                return;
            }

            await FinalizeStreamAsync(existing, modelName, completedAssistantMessages);
        }

        var finalMessage = new AppChatMessage(
            outputText,
            DateTime.Now,
            ChatRole.Assistant,
            BuildStatistics(speakerName, modelName),
            agentId: speakerId,
            agentName: speakerName);
        await AddMessageAsync(finalMessage);
        completedAssistantMessages.Add(new CompletedWorkflowAssistantMessage(
            finalMessage,
            speakerId ?? speakerName));
    }

    private async Task<StreamingAppChatMessage> GetOrCreateStreamAsync(
        string? executorId,
        string outputText,
        string modelName,
        WorkflowPassStreamingState streamingState,
        List<CompletedWorkflowAssistantMessage> completedAssistantMessages,
        CancellationToken cancellationToken)
    {
        var resolvedSpeakerId = WorkflowSpeakerResolver.ResolveFromExecutorId(
            executorId,
            _agentIdsByExecutorId);
        var resolvedSpeakerName = ResolveSpeakerName(resolvedSpeakerId);
        var existing = _activeStreams.Values.LastOrDefault();
        if (existing is not null)
        {
            _activeSpeakerIdsByStreamId.TryGetValue(existing.Id, out var existingSpeakerId);
            if (ShouldContinueCurrentStream(
                    existing,
                    existingSpeakerId,
                    resolvedSpeakerId,
                    resolvedSpeakerName,
                    outputText))
            {
                UpdateStreamSpeaker(existing, resolvedSpeakerId, resolvedSpeakerName);
                if (!string.IsNullOrWhiteSpace(resolvedSpeakerId))
                {
                    _activeSpeakerIdsByStreamId[existing.Id] = resolvedSpeakerId;
                }

                return existing;
            }

            await FinalizeStreamAsync(existing, modelName, completedAssistantMessages);
        }

        var speakerId = resolvedSpeakerId ?? WorkflowSpeakerResolver.ResolveFromWorkflow(
            _parameters?.Workflow,
            streamingState.NextAssistantMessageIndex);
        var speakerName = resolvedSpeakerName ?? ResolveSpeakerName(speakerId);
        var stream = streamingBridge.Create(speakerId, speakerName);
        _activeStreams[stream.Id] = stream;
        _activeSpeakerIdsByStreamId[stream.Id] = speakerId;
        streamingState.RegisterStartedAssistantMessage();
        await AddMessageAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();
        return stream;
    }

    private async Task FinalizeStreamAsync(
        StreamingAppChatMessage stream,
        string modelName,
        List<CompletedWorkflowAssistantMessage> completedAssistantMessages)
    {
        _activeSpeakerIdsByStreamId.TryGetValue(stream.Id, out var speakerId);

        var finalMessage = streamingBridge.Complete(
            stream,
            BuildStatistics(stream.AgentName, modelName));

        ReplaceMessage(stream, finalMessage);
        _activeStreams.Remove(stream.Id);
        _activeSpeakerIdsByStreamId.Remove(stream.Id);

        var resolvedSpeakerId = speakerId;
        if (string.IsNullOrWhiteSpace(resolvedSpeakerId) &&
            !string.IsNullOrWhiteSpace(finalMessage.AgentName))
        {
            _agentIdsByName.TryGetValue(finalMessage.AgentName, out resolvedSpeakerId);
        }

        if (!string.IsNullOrWhiteSpace(resolvedSpeakerId) &&
            !string.Equals(finalMessage.AgentId, resolvedSpeakerId, StringComparison.OrdinalIgnoreCase))
        {
            finalMessage.AgentId = resolvedSpeakerId;
        }

        completedAssistantMessages.Add(new CompletedWorkflowAssistantMessage(
            finalMessage,
            resolvedSpeakerId ?? finalMessage.AgentName));
        await (MessageUpdated?.Invoke(finalMessage, true) ?? Task.CompletedTask);
    }

    private static List<ChatMessage> BuildConversation(IEnumerable<IAppChatMessage> messages)
    {
        List<ChatMessage> result = [];

        foreach (var message in messages.Where(static message => !message.IsStreaming))
        {
            var content = message.Role == ChatRole.User
                ? BuildUserMessage(message.Content ?? string.Empty, message.Files)
                : message.Content?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            result.Add(new ChatMessage(message.Role, content));
        }

        return result;
    }

    private static string BuildUserMessage(string text, IReadOnlyList<AppChatMessageFile>? files)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (files is null || files.Count == 0)
        {
            return trimmed;
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(trimmed))
        {
            builder.AppendLine(trimmed);
            builder.AppendLine();
        }

        builder.AppendLine("Attached files:");
        foreach (var file in files)
        {
            builder.AppendLine($"- {file.Name} ({file.ContentType}, {file.Size} bytes)");
        }

        return builder.ToString().Trim();
    }

    private async Task<MarkdownDocumentIntakeResult?> PrepareDocumentAsync(
        WorkflowStartInputDefinition definition,
        OrchestrationWorkflowStartInputValue input,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(input.Value))
        {
            return documentIntakeService.PrepareMarkdown(input.Value, definition.DisplayName);
        }

        if (!string.IsNullOrWhiteSpace(input.SourceFile))
        {
            return await documentIntakeService.ReadDocumentAsync(input.SourceFile, cancellationToken);
        }

        return null;
    }

    private static void ValidateWorkflowDefinition(
        IOrchestrationWorkflowDefinition workflow,
        IReadOnlyList<ResolvedChatAgent> agents)
    {
        var workflowAgentIds = workflow.Agents
            .Select(static agent => agent.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resolvedAgentIds = agents
            .Select(static agent => agent.Agent.AgentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!workflowAgentIds.SetEquals(resolvedAgentIds))
        {
            throw new InvalidOperationException(
                "Resolved workflow agents do not match the workflow definition.");
        }
    }

    private static IReadOnlyList<OrchestrationWorkflowStartInputValue> NormalizeStartInputs(
        IOrchestrationWorkflowDefinition workflow,
        IReadOnlyList<OrchestrationWorkflowStartInputValue> providedInputs)
    {
        var definitionsByKey = workflow.StartInputs.ToDictionary(
            static input => input.Key,
            StringComparer.OrdinalIgnoreCase);
        var providedByKey = providedInputs.ToDictionary(
            static input => input.Key,
            StringComparer.OrdinalIgnoreCase);

        var unknownKey = providedByKey.Keys.FirstOrDefault(key => !definitionsByKey.ContainsKey(key));
        if (unknownKey is not null)
        {
            throw new InvalidOperationException(
                $"Workflow start input '{unknownKey}' is not defined.");
        }

        List<OrchestrationWorkflowStartInputValue> normalizedInputs = [];

        foreach (var definition in workflow.StartInputs)
        {
            providedByKey.TryGetValue(definition.Key, out var provided);

            if (definition.Kind == WorkflowStartInputKind.MarkdownDocument)
            {
                if (provided is not null &&
                    (!string.IsNullOrWhiteSpace(provided.Value) ||
                     !string.IsNullOrWhiteSpace(provided.SourceFile)))
                {
                    normalizedInputs.Add(new OrchestrationWorkflowStartInputValue
                    {
                        Key = definition.Key,
                        Value = provided.Value,
                        SourceFile = provided.SourceFile
                    });
                    continue;
                }

                if (definition.IsRequired)
                {
                    throw new InvalidOperationException(
                        $"Workflow start input '{definition.DisplayName}' is required.");
                }

                continue;
            }

            var value = provided?.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                value = definition.DefaultValue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                if (definition.IsRequired)
                {
                    throw new InvalidOperationException(
                        $"Workflow start input '{definition.DisplayName}' is required.");
                }

                continue;
            }

            normalizedInputs.Add(new OrchestrationWorkflowStartInputValue
            {
                Key = definition.Key,
                Value = value
            });
        }

        return normalizedInputs;
    }

    private static string NormalizeParameterValue(
        WorkflowStartInputDefinition definition,
        OrchestrationWorkflowStartInputValue input)
    {
        var value = input.Value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Workflow start input '{definition.DisplayName}' requires a value.");
        }

        return definition.Kind switch
        {
            WorkflowStartInputKind.Text => value,
            WorkflowStartInputKind.Number => value,
            WorkflowStartInputKind.Boolean => bool.TryParse(value, out var boolValue)
                ? boolValue ? bool.TrueString.ToLowerInvariant() : bool.FalseString.ToLowerInvariant()
                : throw new InvalidOperationException(
                    $"Workflow start input '{definition.DisplayName}' expects a boolean value."),
            WorkflowStartInputKind.Json => value,
            WorkflowStartInputKind.MarkdownDocument => throw new InvalidOperationException(
                $"Workflow start input '{definition.DisplayName}' is a document and cannot be stored as a parameter."),
            _ => throw new InvalidOperationException(
                $"Workflow start input '{definition.DisplayName}' uses an unsupported input kind '{definition.Kind}'.")
        };
    }

    private static string MapParameterValueKind(WorkflowStartInputKind kind) =>
        kind switch
        {
            WorkflowStartInputKind.Text => "text",
            WorkflowStartInputKind.Number => "number",
            WorkflowStartInputKind.Boolean => "boolean",
            WorkflowStartInputKind.Json => "json",
            WorkflowStartInputKind.MarkdownDocument => throw new InvalidOperationException(
                "Document inputs must be stored as task session documents."),
            _ => throw new InvalidOperationException(
                $"Unsupported workflow start input kind '{kind}'.")
        };

    private static string? TryGetAgentExecutorId(AIAgent agent)
    {
        if (GetDescriptiveIdMethod.Value?.Invoke(null, [agent]) is string descriptiveId &&
            !string.IsNullOrWhiteSpace(descriptiveId))
        {
            return descriptiveId;
        }

        return TryGetStringProperty(agent, "Id") ?? TryGetStringProperty(agent, "Name");
    }

    private static string? TryGetStringProperty(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance);
        if (property?.PropertyType != typeof(string))
        {
            return null;
        }

        return property.GetValue(value) as string;
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

    private async Task ValidateResolvedAgentsAsync(
        IReadOnlyList<ResolvedChatAgent> resolvedAgents,
        CancellationToken cancellationToken)
    {
        foreach (var resolvedAgent in resolvedAgents)
        {
            if (resolvedAgent.Model.ServerId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Server is not resolved for agent '{resolvedAgent.Agent.AgentName}'.");
            }

            if (string.IsNullOrWhiteSpace(resolvedAgent.Model.ModelName))
            {
                throw new InvalidOperationException(
                    $"Model is not resolved for agent '{resolvedAgent.Agent.AgentName}'.");
            }

            await modelCapabilityService.EnsureModelSupportedByServerAsync(
                resolvedAgent.Model,
                cancellationToken);

            if (resolvedAgent.Agent.McpServerBindings.Count == 0)
            {
                continue;
            }

            var supportsFunctions = await modelCapabilityService.SupportsFunctionCallingAsync(
                resolvedAgent.Model,
                cancellationToken);
            if (!supportsFunctions)
            {
                throw new InvalidOperationException(
                    $"Workflow agent '{resolvedAgent.Agent.AgentName}' requires a model with function calling.");
            }
        }
    }

    private static ResolvedChatAgent BindTaskSession(ResolvedChatAgent source, string sessionId)
    {
        var runtimeAgent = source.Agent.Clone();

        foreach (var binding in runtimeAgent.McpServerBindings)
        {
            if (!string.Equals(binding.ServerName, BuiltInTaskSessionMcpServerTools.Descriptor.Name, StringComparison.OrdinalIgnoreCase) &&
                binding.ServerId != BuiltInTaskSessionMcpServerTools.Descriptor.Id)
            {
                continue;
            }

            binding.Parameters[TaskSessionStore.SessionIdParameter] = sessionId;
        }

        return new ResolvedChatAgent(runtimeAgent, source.Model);
    }

    private static AgentDescription CreateRuntimeAgentDescription(ResolvedChatAgent resolvedAgent)
    {
        return AgentDescriptionFactory.CreateRuntime(resolvedAgent.Agent, resolvedAgent.Model);
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
            if (message.IsStreaming || message.Role != ChatRole.Assistant)
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
        await AddMessageAsync(new AppChatMessage(text, DateTime.Now, ChatRole.Assistant));
    }

    private void UpdateAnsweringState(bool isAnswering)
    {
        IsAnswering = isAnswering;
        AnsweringStateChanged?.Invoke(isAnswering);
    }

    private static string BuildStatistics(string? agentName, string modelName)
    {
        if (string.IsNullOrWhiteSpace(agentName))
        {
            return $"workflow orchestration | model {modelName}";
        }

        return $"workflow orchestration | speaker {agentName} | model {modelName}";
    }

    private static void UpdateStreamSpeaker(
        StreamingAppChatMessage stream,
        string? speakerId,
        string? speakerName)
    {
        if (!string.IsNullOrWhiteSpace(speakerId) &&
            !string.Equals(stream.AgentId, speakerId, StringComparison.Ordinal))
        {
            stream.SetAgentId(speakerId);
        }

        if (!string.IsNullOrWhiteSpace(speakerName) &&
            !string.Equals(stream.AgentName, speakerName, StringComparison.Ordinal))
        {
            stream.SetAgentName(speakerName);
        }
    }

    private static bool ShouldContinueCurrentStream(
        StreamingAppChatMessage existing,
        string? existingSpeakerId,
        string? nextSpeakerId,
        string? nextSpeakerName,
        string outputText)
    {
        if (!string.IsNullOrWhiteSpace(nextSpeakerId) ||
            !string.IsNullOrWhiteSpace(nextSpeakerName))
        {
            return IsSameSpeaker(
                existingSpeakerId,
                existing.AgentName,
                nextSpeakerId,
                nextSpeakerName);
        }

        return WorkflowStreamingTextDelta.IsDuplicateOfCurrentMessage(existing.Content, outputText);
    }

    private static bool IsSameSpeaker(
        string? existingSpeakerId,
        string? existingSpeakerName,
        string? nextSpeakerId,
        string? nextSpeakerName)
    {
        if (!string.IsNullOrWhiteSpace(existingSpeakerId) &&
            !string.IsNullOrWhiteSpace(nextSpeakerId))
        {
            return string.Equals(existingSpeakerId, nextSpeakerId, StringComparison.OrdinalIgnoreCase);
        }

        if ((!string.IsNullOrWhiteSpace(existingSpeakerId) ||
             !string.IsNullOrWhiteSpace(nextSpeakerId)) &&
            !string.IsNullOrWhiteSpace(existingSpeakerName) &&
            !string.IsNullOrWhiteSpace(nextSpeakerName))
        {
            return string.Equals(existingSpeakerName, nextSpeakerName, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(existingSpeakerName, nextSpeakerName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CompletedWorkflowAssistantMessage(
        AppChatMessage Message,
        string? SpeakerId);

    private sealed class WorkflowPassStreamingState(int assistantMessagesBeforePass)
    {
        private int _startedAssistantMessages;

        public int NextAssistantMessageIndex => assistantMessagesBeforePass + _startedAssistantMessages;

        public void RegisterStartedAssistantMessage()
        {
            _startedAssistantMessages++;
        }
    }

    private sealed record WorkflowPassResult(
        IReadOnlyList<CompletedWorkflowAssistantMessage> CompletedAssistantMessages);
}
