using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Collections.ObjectModel;
using System.Text;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class UnifiedAgentRuntimeChatSessionService(
    IAgentRunner agentRunner,
    IAgentDefinitionCatalog definitionCatalog,
    IAgentRunContextFactory runContextFactory,
    IChatEngineStreamingBridge streamingBridge,
    ILogger<UnifiedAgentRuntimeChatSessionService> logger,
    IAgentTemplateService agentTemplateService,
    AgenticRuntimeAgentFactory runtimeAgentFactory,
    HarnessResponseEventProjector responseEventProjector) : IChatEngineSessionService
{
    private readonly AppChat _chat = new();
    private readonly Dictionary<string, StreamingAppChatMessage> _activeStreamsByRuntimeMessageId =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedRuntimeMessageIds = new(StringComparer.Ordinal);
    private ChatEngineSessionStartRequest? _parameters;
    private CancellationTokenSource? _cancellationTokenSource;
    private AIAgent? _directAgent;
    private AgentSession? _directSession;
    private IReadOnlyDictionary<string, AgenticRegisteredTool> _directToolMetadata =
        new Dictionary<string, AgenticRegisteredTool>(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource? _activeRunCompletion;
    private readonly object _lifecycleLock = new();
    private readonly SemaphoreSlim _runSetupGate = new(1, 1);
    private long _generation;
    private bool _resetting;

    public event Action<bool>? AnsweringStateChanged;
    public event Action? ChatReset;
    public event Func<IAppChatMessage, Task>? MessageAdded;
    public event Func<IAppChatMessage, bool, Task>? MessageUpdated;

    public bool IsAnswering { get; private set; }

    public bool RequiresReset { get; private set; }

    public Guid Id => _chat.Id;

    public IReadOnlyCollection<AgentExecutionSpec> Agents => _chat.Agents;

    public ObservableCollection<IAppChatMessage> Messages => _chat.Messages;

    IReadOnlyCollection<IAppChatMessage> IChatSessionService.Messages => _chat.Messages;

    public async Task StartAsync(
        ChatEngineSessionStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RuntimeReference is null && request.Agents.Count == 0)
        {
            throw new ArgumentException(
                "A runtime reference or at least one resolved agent must be provided.",
                nameof(request));
        }

        await ResetAsync(cancellationToken);

        _parameters = request.Snapshot();
        _chat.Reset();
        ClearRunLocalState();
        _chat.SetAgents(request.RuntimeParticipant is { } participant
            ? [new AgentExecutionSpec
            {
                RuntimeAgentId = participant.Id,
                AgentName = participant.Name,
                Summary = participant.Description,
                ShortName = participant.AvatarText
            }]
            : request.Agents.Select(static agent => agent.Agent.Clone()));
        ChatReset?.Invoke();

        if (request.RuntimeReference?.Kind == AgentDefinitionKind.SavedAgent)
        {
            await CreateDirectConversationAsync(request, cancellationToken);
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        Task? activeRun;
        lock (_lifecycleLock)
        {
            _resetting = true;
        }

        await _runSetupGate.WaitAsync(cancellationToken);
        try
        {
            lock (_lifecycleLock)
            {
                Interlocked.Increment(ref _generation);
                _cancellationTokenSource?.Cancel();
                activeRun = _activeRunCompletion?.Task;
            }
        }
        finally
        {
            _runSetupGate.Release();
        }

        if (activeRun is not null)
        {
            await activeRun.WaitAsync(cancellationToken);
        }

        lock (_lifecycleLock)
        {
            _directAgent = null;
            _directSession = null;
            _directToolMetadata = new Dictionary<string, AgenticRegisteredTool>(StringComparer.OrdinalIgnoreCase);
            _chat.Reset();
            ClearRunLocalState();
            _parameters = null;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _activeRunCompletion = null;
            RequiresReset = false;
            _resetting = false;
        }

        UpdateAnsweringState(false);
        ChatReset?.Invoke();
    }

    public async Task CancelAsync()
    {
        _cancellationTokenSource?.Cancel();
        Task? activeRun;
        lock (_lifecycleLock)
        {
            activeRun = _activeRunCompletion?.Task;
        }

        if (activeRun is not null)
        {
            await activeRun;
        }
    }

    public async Task SendAsync(
        string text,
        IReadOnlyList<AppChatMessageFile>? files = null,
        CancellationToken cancellationToken = default)
    {
        if (_parameters is null)
        {
            throw new InvalidOperationException("Chat session not started.");
        }

        if (string.IsNullOrWhiteSpace(text) || IsAnswering || _resetting)
        {
            return;
        }

        if (RequiresReset)
        {
            throw new InvalidOperationException("This conversation cannot continue after a canceled or failed run. Start a new chat.");
        }

        if (_parameters.RuntimeReference is null)
        {
            throw new InvalidOperationException("Unified agent runtime reference is not configured.");
        }

        await _runSetupGate.WaitAsync(cancellationToken);
        long generation;
        try
        {
            if (_resetting || IsAnswering)
            {
                return;
            }

            if (RequiresReset)
            {
                throw new InvalidOperationException("This conversation cannot continue after a canceled or failed run. Start a new chat.");
            }

            generation = Interlocked.Read(ref _generation);
            if (_parameters.RuntimeReference.Kind == AgentDefinitionKind.SavedAgent)
            {
                await EnsureDirectConversationAsync(cancellationToken);
            }

            if (generation != Interlocked.Read(ref _generation))
            {
                return;
            }

            var userMessage = new AppChatMessage(text, DateTime.Now, AppChatRole.User, files: files ?? []);
            await AddMessageAsync(userMessage);
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeRunCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            UpdateAnsweringState(true);
        }
        finally
        {
            _runSetupGate.Release();
        }

        if (_parameters.RuntimeReference.Kind == AgentDefinitionKind.SavedAgent)
        {
            await SendDirectAsync(text, files ?? [], generation, cancellationToken);
            return;
        }

        var terminalFailureHandled = false;

        try
        {
            var runtimeRequest = new AgentRuntimeRunRequest
            {
                Messages = _chat.Messages
                    .Where(static message => !message.IsStreaming)
                    .Where(static message => message.Role is AppChatRole.System or AppChatRole.User or AppChatRole.Assistant)
                    .Select(static message => new AgentInputMessage(
                        message.Role switch
                        {
                            AppChatRole.System => AgentMessageRole.System,
                            AppChatRole.Assistant => AgentMessageRole.Assistant,
                            _ => AgentMessageRole.User
                        },
                        message.Content))
                    .ToList(),
                Inputs = new Dictionary<string, string>(
                    _parameters.RuntimeInputs,
                    StringComparer.OrdinalIgnoreCase),
                Attachments = (files ?? [])
                    .Select(ToAgentInputAttachment)
                    .ToList()
            };

            var creationContext = new AgentRuntimeCreationContext
            {
                Configuration = _parameters.Configuration,
                DefaultModel = _parameters.RuntimeDefaultModel ?? _parameters.Agents.FirstOrDefault()?.Model,
                Overrides = _parameters.Overrides
            };
            var descriptor = await definitionCatalog.GetRequiredAsync(
                _parameters.RuntimeReference,
                _cancellationTokenSource.Token);
            var runContext = runContextFactory.CreateRoot(
                descriptor,
                _chat.Id.ToString("N"));

            await foreach (var runEvent in agentRunner.RunAsync(
                               _parameters.RuntimeReference,
                               runtimeRequest,
                               creationContext,
                               runContext,
                               _cancellationTokenSource!.Token))
            {
                if (runEvent is AgentRunFailed)
                {
                    terminalFailureHandled = true;
                }

                if (generation != Interlocked.Read(ref _generation))
                {
                    break;
                }

                await ApplyRunEventAsync(runEvent, generation, _cancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
            if (generation == Interlocked.Read(ref _generation))
            {
                RequiresReset = true;
                await CancelActiveStreamsAsync();
            }
        }
        catch (Exception ex)
        {
            if (generation == Interlocked.Read(ref _generation))
            {
                RequiresReset = true;
                logger.LogError(ex, "Unified agent chat run failed.");
                await CancelActiveStreamsAsync();
                if (!terminalFailureHandled)
                {
                    await AddMessageAsync(new AppChatMessage(
                        "Agent runtime error: The run failed before a terminal result was produced.",
                        DateTime.Now,
                        AppChatRole.Assistant));
                }
            }
        }
        finally
        {
            if (generation == Interlocked.Read(ref _generation))
            {
                ClearRunLocalState();
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                UpdateAnsweringState(false);
            }

            _activeRunCompletion?.TrySetResult();
        }
    }

    private async Task CreateDirectConversationAsync(
        ChatEngineSessionStartRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.RuntimeReference!.Id, out var templateId) ||
            request.RuntimeDefaultModel is null)
        {
            throw new InvalidOperationException("The saved agent and model must be resolved before starting a conversation.");
        }

        var template = await agentTemplateService.GetByIdAsync(templateId)
            ?? throw new InvalidOperationException($"Saved agent '{request.RuntimeReference.Id}' was not found.");
        if (request.Overrides.McpServerBindings is not null)
        {
            template = template.Clone();
            template.McpServerBindings = request.Overrides.McpServerBindings
                .Select(static binding => binding.Clone())
                .ToList();
        }

        var resolved = ResolvedChatAgentFactory.Resolve(template, request.RuntimeDefaultModel);
        var build = await runtimeAgentFactory.CreateAsync(new AgentRunRequest
        {
            Agent = resolved.Agent,
            ResolvedModel = resolved.Model,
            Configuration = request.Configuration,
            Conversation = [],
            UserMessage = string.Empty
        }, cancellationToken: cancellationToken);

        _directAgent = build.Agent;
        _directSession = await build.Agent.CreateSessionAsync(cancellationToken);
        _directToolMetadata = build.ToolSet.MetadataByName;
    }

    private async Task EnsureDirectConversationAsync(CancellationToken cancellationToken)
    {
        if (_parameters is null)
        {
            throw new InvalidOperationException("Chat session not started.");
        }

        if (_directAgent is not null && _directSession is not null)
        {
            return;
        }

        await CreateDirectConversationAsync(_parameters, cancellationToken);
    }

    private async Task SendDirectAsync(
        string text,
        IReadOnlyList<AppChatMessageFile> files,
        long generation,
        CancellationToken cancellationToken)
    {
        if (generation != Interlocked.Read(ref _generation))
        {
            return;
        }

        var messageId = $"direct-harness-response-{Guid.NewGuid():N}";
        var stream = await GetOrCreateStreamAsync(messageId, _chat.Agents.FirstOrDefault()?.AgentName ?? "Agent");
        var projection = responseEventProjector.CreateProjection();

        try
        {
            await foreach (var update in _directAgent!.RunStreamingAsync(
                               [BuildDirectUserMessage(text, files)],
                               _directSession,
                               BuildDirectRunOptions(),
                               _cancellationTokenSource.Token))
            {
                if (generation != Interlocked.Read(ref _generation))
                {
                    break;
                }

                foreach (var responseEvent in projection.Project(update, _directToolMetadata))
                {
                    if (generation != Interlocked.Read(ref _generation))
                    {
                        break;
                    }

                    ApplyHarnessEvent(stream, responseEvent);
                    await (MessageUpdated?.Invoke(stream, false) ?? Task.CompletedTask);
                }
            }

            if (generation != Interlocked.Read(ref _generation))
            {
                return;
            }

            var final = streamingBridge.Complete(stream, "HarnessAgent");
            ReplaceMessage(stream, final);
            await (MessageUpdated?.Invoke(final, true) ?? Task.CompletedTask);
            _activeStreamsByRuntimeMessageId.Remove(messageId);
        }
        catch (OperationCanceledException)
        {
            if (generation == Interlocked.Read(ref _generation))
            {
                RequiresReset = true;
                _directSession = null;
                await CancelActiveStreamsAsync();
            }
        }
        catch (Exception ex)
        {
            if (generation == Interlocked.Read(ref _generation))
            {
                RequiresReset = true;
                _directSession = null;
                logger.LogError(ex, "Harness direct chat run failed.");
                await CancelActiveStreamsAsync();
                await AddMessageAsync(new AppChatMessage($"Agent runtime error: {ex.Message}", DateTime.Now, AppChatRole.Assistant));
            }
        }
        finally
        {
            if (generation == Interlocked.Read(ref _generation))
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                UpdateAnsweringState(false);
            }

            _activeRunCompletion?.TrySetResult();
        }
    }

    private ChatClientAgentRunOptions BuildDirectRunOptions()
    {
        if (_parameters?.RuntimeDefaultModel is null)
        {
            return new ChatClientAgentRunOptions(new ChatOptions());
        }

        var agent = _chat.Agents.FirstOrDefault();
        var options = new ChatOptions
        {
            ModelId = _parameters.RuntimeDefaultModel.ModelName,
            Temperature = agent?.Temperature is double temperature
                ? (float)temperature
                : null
        };

        if (agent?.RepeatPenalty is double repeatPenalty)
        {
            options.AdditionalProperties ??= [];
            options.AdditionalProperties["repeat_penalty"] = repeatPenalty;
        }

        return new ChatClientAgentRunOptions(options);
    }

    private static ChatMessage BuildDirectUserMessage(
        string text,
        IReadOnlyList<AppChatMessageFile> files)
    {
        if (files.Count == 0)
        {
            return new ChatMessage(ChatRole.User, text);
        }

        List<AIContent> contents = [new TextContent(text)];
        foreach (var file in files)
        {
            if (IsTextAttachment(file))
            {
                contents.Add(new TextContent(Encoding.UTF8.GetString(file.Data)));
                continue;
            }

            contents.Add(new DataContent(file.Data, file.ContentType));
        }

        return new ChatMessage(ChatRole.User, contents);
    }

    private async Task ApplyRunEventAsync(
        AgentRunEvent runEvent,
        long generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Interlocked.Read(ref _generation))
        {
            return;
        }

        switch (runEvent)
        {
            case AgentTextDelta delta:
                var stream = await GetOrCreateStreamAsync(delta.MessageId, delta.Author);
                streamingBridge.Append(stream, delta.Text);
                await (MessageUpdated?.Invoke(stream, false) ?? Task.CompletedTask);
                break;

            case AgentToolCallStarted started:
                await ApplyToolInvocationAsync(started.MessageId, started.Author, started.Invocation);
                break;

            case AgentToolCallCompleted completed:
                await ApplyToolInvocationAsync(completed.MessageId, completed.Author, completed.Invocation);
                break;

            case AgentToolCallFailed failed:
                await ApplyToolInvocationAsync(failed.MessageId, failed.Author, failed.Invocation);
                break;

            case AgentMessageCompleted completed:
                await CompleteOrAddAssistantMessageAsync(completed.MessageId, completed.Message);
                break;

            case AgentRunCompleted completed:
                await ApplyFinalResultAsync(completed.Result);
                await CompleteRemainingStreamsAsync();
                break;

            case AgentRunFailed failed:
                await AddFailureAsync(failed.Error);
                break;
        }
    }

    private async Task CompleteOrAddAssistantMessageAsync(
        string runtimeMessageId,
        AgentOutputMessage output)
    {
        if (_activeStreamsByRuntimeMessageId.TryGetValue(runtimeMessageId, out var stream))
        {
            if (!string.IsNullOrWhiteSpace(output.Author))
            {
                stream.SetAgentId(output.Author);
                stream.SetAgentName(output.Author);
            }

            var final = streamingBridge.Complete(stream, output.Content, "unified agent runtime");
            ReplaceMessage(stream, final);
            await (MessageUpdated?.Invoke(final, true) ?? Task.CompletedTask);
            _activeStreamsByRuntimeMessageId.Remove(runtimeMessageId);
            _completedRuntimeMessageIds.Add(runtimeMessageId);
            return;
        }

        await AddMessageAsync(new AppChatMessage(
            output.Content,
            DateTime.Now,
            AppChatRole.Assistant,
            agentId: output.Author,
            agentName: output.Author));
        _completedRuntimeMessageIds.Add(runtimeMessageId);
    }

    private async Task ApplyToolInvocationAsync(
        string runtimeMessageId,
        string author,
        ToolInvocationViewState invocation)
    {
        var stream = await GetOrCreateStreamAsync(runtimeMessageId, author);
        stream.UpdateToolInvocation(invocation);
        await (MessageUpdated?.Invoke(stream, true) ?? Task.CompletedTask);
    }

    private static void ApplyHarnessEvent(
        StreamingAppChatMessage stream,
        HarnessResponseEvent responseEvent)
    {
        switch (responseEvent)
        {
            case HarnessTextDelta text:
                stream.Append(text.Text);
                break;

            case HarnessToolCallStarted started:
                stream.StartToolInvocation(ToViewState(started));
                break;

            case HarnessToolCallCompleted completed:
                stream.UpdateToolInvocation(ToViewState(completed));
                break;

            case HarnessToolCallFailed failed:
                stream.UpdateToolInvocation(ToViewState(failed));
                break;
        }
    }

    private static ToolInvocationViewState ToViewState(HarnessToolCallStarted value) => new(
        value.CallId, value.RegisteredName, value.OriginalName, value.Source, value.ServerName,
        value.BindingName, value.IsInteractive, value.Arguments, null, null,
        ToolInvocationStatus.Running, value.StartedAt, null);

    private static ToolInvocationViewState ToViewState(HarnessToolCallCompleted value) => new(
        value.CallId, value.RegisteredName, value.OriginalName, value.Source, value.ServerName,
        value.BindingName, value.IsInteractive, value.Arguments, value.Result, null,
        ToolInvocationStatus.Succeeded, value.StartedAt, value.CompletedAt);

    private static ToolInvocationViewState ToViewState(HarnessToolCallFailed value) => new(
        value.CallId, value.RegisteredName, value.OriginalName, value.Source, value.ServerName,
        value.BindingName, value.IsInteractive, value.Arguments, null, value.Error,
        ToolInvocationStatus.Failed, value.StartedAt, value.CompletedAt);

    private async Task ApplyFinalResultAsync(AgentRunResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.FinalMessageId) &&
            _completedRuntimeMessageIds.Contains(result.FinalMessageId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.FinalMessageId))
        {
            await CompleteOrAddAssistantMessageAsync(result.FinalMessageId, result.FinalMessage);
            return;
        }

        await AddMessageAsync(new AppChatMessage(
            result.FinalMessage.Content,
            DateTime.Now,
            AppChatRole.Assistant,
            agentId: result.FinalMessage.Author,
            agentName: result.FinalMessage.Author));
    }

    private async Task AddFailureAsync(AgentRunError error)
    {
        await CancelActiveStreamsAsync();
        await AddMessageAsync(new AppChatMessage(
            $"Agent runtime error: {error.Message}",
            DateTime.Now,
            AppChatRole.Assistant));
    }

    private async Task<StreamingAppChatMessage> GetOrCreateStreamAsync(
        string runtimeMessageId,
        string author)
    {
        if (_activeStreamsByRuntimeMessageId.TryGetValue(runtimeMessageId, out var existing))
        {
            if (!string.IsNullOrWhiteSpace(author))
            {
                existing.SetAgentId(author);
                existing.SetAgentName(author);
            }

            return existing;
        }

        var stream = streamingBridge.Create(author, author);
        _activeStreamsByRuntimeMessageId[runtimeMessageId] = stream;
        await AddMessageAsync(stream);
        return stream;
    }

    private async Task CompleteRemainingStreamsAsync()
    {
        foreach (var pair in _activeStreamsByRuntimeMessageId.ToList())
        {
            if (string.IsNullOrWhiteSpace(pair.Value.Content) && pair.Value.ToolInvocations.Count == 0)
            {
                _activeStreamsByRuntimeMessageId.Remove(pair.Key);
                continue;
            }

            var final = streamingBridge.Complete(pair.Value, "unified agent runtime");
            ReplaceMessage(pair.Value, final);
            await (MessageUpdated?.Invoke(final, true) ?? Task.CompletedTask);
            _completedRuntimeMessageIds.Add(pair.Key);
            _activeStreamsByRuntimeMessageId.Remove(pair.Key);
        }
    }

    private async Task CancelActiveStreamsAsync()
    {
        foreach (var stream in _activeStreamsByRuntimeMessageId.Values.ToList())
        {
            foreach (var invocation in stream.ToolInvocations
                         .Where(static invocation => invocation.Status == ToolInvocationStatus.Running)
                         .ToList())
            {
                stream.UpdateToolInvocation(invocation with
                {
                    Status = ToolInvocationStatus.Canceled,
                    Error = "Canceled",
                    CompletedAt = DateTimeOffset.UtcNow
                });
            }

            var canceled = streamingBridge.Cancel(stream);
            ReplaceMessage(stream, canceled);
            await (MessageUpdated?.Invoke(canceled, true) ?? Task.CompletedTask);
        }

        _activeStreamsByRuntimeMessageId.Clear();
    }

    private void ClearRunLocalState()
    {
        _activeStreamsByRuntimeMessageId.Clear();
        _completedRuntimeMessageIds.Clear();
    }

    private async Task AddMessageAsync(IAppChatMessage message)
    {
        if (_chat.Messages.Any(existing => existing.Id == message.Id))
        {
            return;
        }

        _chat.Messages.Add(message);
        await (MessageAdded?.Invoke(message) ?? Task.CompletedTask);
    }

    private void ReplaceMessage(
        IAppChatMessage source,
        IAppChatMessage replacement)
    {
        var index = _chat.Messages.IndexOf(source);
        if (index >= 0)
        {
            _chat.Messages[index] = replacement;
            return;
        }

        _chat.Messages.Add(replacement);
    }

    private void UpdateAnsweringState(bool isAnswering)
    {
        IsAnswering = isAnswering;
        AnsweringStateChanged?.Invoke(isAnswering);
    }

    private static string ResolveRuntimeDisplayName(ChatEngineSessionStartRequest request) =>
        request.Agents.FirstOrDefault()?.Agent.AgentName ??
        request.RuntimeReference?.Kind.ToString() ??
        "Agent";

    private static AgentInputAttachment ToAgentInputAttachment(AppChatMessageFile file)
    {
        var content = IsTextAttachment(file)
            ? Encoding.UTF8.GetString(file.Data)
            : Convert.ToBase64String(file.Data);

        return new AgentInputAttachment(file.Name, file.ContentType, content)
        {
            Data = file.Data
        };
    }

    private static bool IsTextAttachment(AppChatMessageFile file) =>
        file.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        file.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
        file.Name.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase) ||
        file.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
}
