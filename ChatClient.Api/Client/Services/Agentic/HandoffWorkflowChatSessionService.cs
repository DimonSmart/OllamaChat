using System.Collections.ObjectModel;
using System.Text;
using ChatClient.Api.AgentWorkflows;
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

public sealed class HandoffWorkflowChatSessionService(
    ILogger<HandoffWorkflowChatSessionService> logger,
    IModelCapabilityService modelCapabilityService,
    TaskSessionStore taskSessionStore,
    MarkdownDocumentIntakeService documentIntakeService,
    AgenticRuntimeAgentFactory runtimeAgentFactory,
    IChatEngineStreamingBridge streamingBridge) : IHandoffWorkflowSessionService
{
    private readonly AppChat _chat = new();
    private readonly Dictionary<Guid, StreamingAppChatMessage> _activeStreams = [];
    private readonly Dictionary<Guid, string?> _activeSpeakerIdsByStreamId = [];
    private readonly Dictionary<string, AIAgent> _workflowAgentsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _agentNamesById = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cancellationTokenSource;
    private HandoffWorkflowSessionStartRequest? _parameters;

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
        HandoffWorkflowSessionStartRequest request,
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
            _agentNamesById.Clear();

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
                _agentNamesById[sessionBoundAgent.Agent.AgentId] = sessionBoundAgent.Agent.AgentName;
            }

            stage = "store-parameters";
            _parameters = new HandoffWorkflowSessionStartRequest
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
            _chat.SetAgents(sessionBoundAgents.Select(CreateRuntimeAgentDescription));
            ChatReset?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to start handoff workflow session at stage {Stage}. WorkflowId={WorkflowId}, AgentCount={AgentCount}, TaskSessionId={TaskSessionId}",
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
            "This session service is workflow-specific. Use StartAsync(HandoffWorkflowSessionStartRequest).");
    }

    public void ResetChat()
    {
        _chat.Reset();
        _activeStreams.Clear();
        _activeSpeakerIdsByStreamId.Clear();
        _workflowAgentsById.Clear();
        _agentNamesById.Clear();
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
            ChatStrategyName = "HandoffWorkflow"
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
        await (MessageDeleted?.Invoke(messageId) ?? Task.CompletedTask);
    }

    private async Task GenerateAnswerAsync(
        string text,
        IReadOnlyList<AppChatMessageFile> files,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || IsAnswering || _parameters is null || string.IsNullOrWhiteSpace(TaskSessionId))
        {
            return;
        }

        var userMessage = new AppChatMessage(text, DateTime.Now, ChatRole.User, files: files);
        await AddMessageAsync(userMessage);
        await taskSessionStore.AppendTurnAsync(TaskSessionId, "user", BuildUserMessage(text, files), "user", cancellationToken);

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
        HandoffWorkflowSessionStartRequest parameters,
        CancellationToken cancellationToken)
    {
        var workflow = BuildWorkflow(parameters.Workflow);
        var conversation = BuildConversation(_chat.Messages);
        var sessionId = TaskSessionId ?? Guid.NewGuid().ToString("N");

        await using var run = await InProcessExecution.RunAsync(
            workflow,
            conversation,
            sessionId,
            cancellationToken);
        var completedAssistantMessages = new List<CompletedWorkflowAssistantMessage>();
        var assistantOutputObserved = await ProcessWorkflowEventsAsync(
            run.NewEvents,
            parameters.Configuration.ModelName,
            completedAssistantMessages,
            cancellationToken);
        var turnTokenSent = false;

        while (true)
        {
            var status = await run.GetStatusAsync(cancellationToken);
            logger.LogDebug(
                "Workflow run batch completed. Status={Status}, NewEventCount={NewEventCount}, TurnTokenSent={TurnTokenSent}, AssistantOutputObserved={AssistantOutputObserved}",
                status,
                run.NewEventCount,
                turnTokenSent,
                assistantOutputObserved);

            switch (status)
            {
                case RunStatus.Ended:
                    break;

                case RunStatus.PendingRequests:
                    throw new InvalidOperationException(
                        "Workflow requested unsupported external input.");

                case RunStatus.Idle when assistantOutputObserved || turnTokenSent:
                    goto WorkflowTurnComplete;

                case RunStatus.Idle:
                    turnTokenSent = true;
                    logger.LogDebug("Resuming workflow run with a turn token.");
                    _ = await run.ResumeAsync(cancellationToken, new[] { new TurnToken(emitEvents: true) });
                    assistantOutputObserved |= await ProcessWorkflowEventsAsync(
                        run.NewEvents,
                        parameters.Configuration.ModelName,
                        completedAssistantMessages,
                        cancellationToken);
                    continue;

                case RunStatus.NotStarted:
                case RunStatus.Running:
                default:
                    throw new InvalidOperationException(
                        $"Workflow returned unexpected run status '{status}'.");
            }

            break;
        }

    WorkflowTurnComplete:
        foreach (var stream in _activeStreams.Values.ToList())
        {
            await FinalizeStreamAsync(
                stream,
                parameters.Configuration.ModelName,
                completedAssistantMessages);
        }

        _activeStreams.Clear();
        _activeSpeakerIdsByStreamId.Clear();

        if (completedAssistantMessages.Count == 0)
        {
            await HandleError("Workflow returned an empty response.");
            return;
        }

        foreach (var completedMessage in completedAssistantMessages)
        {
            await taskSessionStore.AppendTurnAsync(
                TaskSessionId,
                "assistant",
                completedMessage.Message.Content,
                completedMessage.SpeakerId,
                cancellationToken);
        }
    }

    private async Task<bool> ProcessWorkflowEventsAsync(
        IEnumerable<WorkflowEvent> workflowEvents,
        string modelName,
        List<CompletedWorkflowAssistantMessage> completedAssistantMessages,
        CancellationToken cancellationToken)
    {
        var assistantOutputObserved = false;

        foreach (var workflowEvent in workflowEvents)
        {
            switch (workflowEvent)
            {
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

                        var speakerId = ResolveSpeakerId(outputEvent);
                        var speakerName = ResolveSpeakerName(speakerId);
                        await FinalizePreviousStreamIfNeededAsync(
                            speakerId,
                            speakerName,
                            modelName,
                            completedAssistantMessages);

                        var stream = await GetOrCreateStreamAsync(
                            speakerId,
                            speakerName,
                            cancellationToken);
                        if (!string.IsNullOrWhiteSpace(speakerName) &&
                            !string.Equals(stream.AgentName, speakerName, StringComparison.Ordinal))
                        {
                            stream.SetAgentName(speakerName);
                        }

                        var appendText = WorkflowStreamingTextDelta.GetAppendText(stream.Content, outputText);
                        if (string.IsNullOrEmpty(appendText))
                        {
                            continue;
                        }

                        assistantOutputObserved = true;
                        streamingBridge.Append(stream, appendText);
                        await (MessageUpdated?.Invoke(stream, true) ?? Task.CompletedTask);
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

    private Workflow BuildWorkflow(AgentWorkflowDefinition definition)
    {
        if (!_workflowAgentsById.TryGetValue(definition.StartAgentId, out var startAgent))
        {
            throw new InvalidOperationException(
                $"Workflow start agent '{definition.StartAgentId}' was not prepared.");
        }

        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(startAgent);

        foreach (var handoff in definition.Handoffs)
        {
            if (!_workflowAgentsById.TryGetValue(handoff.FromAgentId, out var fromAgent))
            {
                throw new InvalidOperationException(
                    $"Workflow source agent '{handoff.FromAgentId}' was not prepared.");
            }

            if (!_workflowAgentsById.TryGetValue(handoff.ToAgentId, out var toAgent))
            {
                throw new InvalidOperationException(
                    $"Workflow target agent '{handoff.ToAgentId}' was not prepared.");
            }

            builder.WithHandoff(fromAgent, toAgent, handoff.Label);
        }

        return builder.Build();
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

    private string? ResolveSpeakerId(WorkflowOutputEvent outputEvent)
    {
        if (!string.IsNullOrWhiteSpace(outputEvent.ExecutorId) &&
            _agentNamesById.ContainsKey(outputEvent.ExecutorId))
        {
            return outputEvent.ExecutorId;
        }

        return null;
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

    private async Task FinalizePreviousStreamIfNeededAsync(
        string? speakerId,
        string? speakerName,
        string modelName,
        List<CompletedWorkflowAssistantMessage> completedAssistantMessages)
    {
        var existing = _activeStreams.Values.LastOrDefault();
        if (existing is null)
        {
            return;
        }

        _activeSpeakerIdsByStreamId.TryGetValue(existing.Id, out var existingSpeakerId);
        if (IsSameSpeaker(existingSpeakerId, existing.AgentName, speakerId, speakerName))
        {
            return;
        }

        await FinalizeStreamAsync(existing, modelName, completedAssistantMessages);
    }

    private async Task<StreamingAppChatMessage> GetOrCreateStreamAsync(
        string? speakerId,
        string? agentName,
        CancellationToken cancellationToken)
    {
        var existing = _activeStreams.Values.LastOrDefault();
        if (existing is not null)
        {
            _activeSpeakerIdsByStreamId.TryGetValue(existing.Id, out var existingSpeakerId);
            if (IsSameSpeaker(existingSpeakerId, existing.AgentName, speakerId, agentName))
            {
                return existing;
            }
        }

        var stream = streamingBridge.Create(agentName ?? "Workflow");
        _activeStreams[stream.Id] = stream;
        _activeSpeakerIdsByStreamId[stream.Id] = speakerId ?? agentName;
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
        completedAssistantMessages.Add(new CompletedWorkflowAssistantMessage(
            finalMessage,
            speakerId ?? finalMessage.AgentName));
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
        HandoffWorkflowStartInputValue input,
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
        AgentWorkflowDefinition workflow,
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

    private static IReadOnlyList<HandoffWorkflowStartInputValue> NormalizeStartInputs(
        AgentWorkflowDefinition workflow,
        IReadOnlyList<HandoffWorkflowStartInputValue> providedInputs)
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

        List<HandoffWorkflowStartInputValue> normalizedInputs = [];

        foreach (var definition in workflow.StartInputs)
        {
            providedByKey.TryGetValue(definition.Key, out var provided);

            if (definition.Kind == WorkflowStartInputKind.MarkdownDocument)
            {
                if (provided is not null &&
                    (!string.IsNullOrWhiteSpace(provided.Value) ||
                     !string.IsNullOrWhiteSpace(provided.SourceFile)))
                {
                    normalizedInputs.Add(new HandoffWorkflowStartInputValue
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

            normalizedInputs.Add(new HandoffWorkflowStartInputValue
            {
                Key = definition.Key,
                Value = value
            });
        }

        return normalizedInputs;
    }

    private static string NormalizeParameterValue(
        WorkflowStartInputDefinition definition,
        HandoffWorkflowStartInputValue input)
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
            return $"workflow handoff | model {modelName}";
        }

        return $"workflow handoff | speaker {agentName} | model {modelName}";
    }

    private static bool IsSameSpeaker(
        string? existingSpeakerId,
        string? existingSpeakerName,
        string? nextSpeakerId,
        string? nextSpeakerName)
    {
        if (!string.IsNullOrWhiteSpace(existingSpeakerId) ||
            !string.IsNullOrWhiteSpace(nextSpeakerId))
        {
            return string.Equals(existingSpeakerId, nextSpeakerId, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(existingSpeakerName, nextSpeakerName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CompletedWorkflowAssistantMessage(
        AppChatMessage Message,
        string? SpeakerId);
}
