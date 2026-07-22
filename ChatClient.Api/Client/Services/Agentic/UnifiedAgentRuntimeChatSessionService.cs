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
    IAgentTemplateService? agentTemplateService = null,
    AgenticRuntimeAgentFactory? runtimeAgentFactory = null) : IChatEngineSessionService
{
    private readonly AppChat _chat = new();
    private readonly Dictionary<string, StreamingAppChatMessage> _activeStreamsByRuntimeMessageId =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedRuntimeMessageIds = new(StringComparer.Ordinal);
    private ChatEngineSessionStartRequest? _parameters;
    private CancellationTokenSource? _cancellationTokenSource;
    private AIAgent? _directAgent;
    private AgentSession? _directSession;

    public event Action<bool>? AnsweringStateChanged;
    public event Action? ChatReset;
    public event Func<IAppChatMessage, Task>? MessageAdded;
    public event Func<IAppChatMessage, bool, Task>? MessageUpdated;
    public event Func<Guid, Task>? MessageDeleted;

    public bool IsAnswering { get; private set; }

    public Guid Id => _chat.Id;

    public IReadOnlyCollection<AgentExecutionSpec> Agents => _chat.Agents;

    public ObservableCollection<IAppChatMessage> Messages => _chat.Messages;

    IReadOnlyCollection<IAppChatMessage> IChatSessionService.Messages => _chat.Messages;

    public ChatEngineSessionStartRequest? CurrentStartRequest => _parameters?.Snapshot();

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
            if (request.History.Count > 0)
            {
                throw new InvalidOperationException("Saved chat history cannot be restored. Start a new conversation instead.");
            }

            await CreateDirectConversationAsync(request, cancellationToken);
        }

        foreach (var message in request.History.OrderBy(static message => message.MsgDateTime))
        {
            _chat.Messages.Add(message);
            await (MessageAdded?.Invoke(message) ?? Task.CompletedTask);
        }

        await Task.CompletedTask;
    }

    public void ResetChat()
    {
        _directAgent = null;
        _directSession = null;
        _chat.Reset();
        ClearRunLocalState();
        _parameters = null;
        ChatReset?.Invoke();
    }

    public async Task CancelAsync()
    {
        _cancellationTokenSource?.Cancel();

        foreach (var stream in _activeStreamsByRuntimeMessageId.Values.ToList())
        {
            var canceled = streamingBridge.Cancel(stream);
            ReplaceMessage(stream, canceled);
            await (MessageUpdated?.Invoke(canceled, true) ?? Task.CompletedTask);
        }

        ClearRunLocalState();
        UpdateAnsweringState(false);
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

        if (string.IsNullOrWhiteSpace(text) || IsAnswering)
        {
            return;
        }

        if (_parameters.RuntimeReference is null)
        {
            throw new InvalidOperationException("Unified agent runtime reference is not configured.");
        }

        if (_directAgent is not null && _directSession is not null)
        {
            await SendDirectAsync(text, files ?? [], cancellationToken);
            return;
        }

        var userMessage = new AppChatMessage(text, DateTime.Now, AppChatRole.User, files: files ?? []);
        await AddMessageAsync(userMessage);

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        UpdateAnsweringState(true);
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
                               _cancellationTokenSource.Token))
            {
                if (runEvent is AgentRunFailed)
                {
                    terminalFailureHandled = true;
                }

                await ApplyRunEventAsync(runEvent, _cancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
            await CancelActiveStreamsAsync();
        }
        catch (Exception ex)
        {
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
        finally
        {
            ClearRunLocalState();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            UpdateAnsweringState(false);
        }
    }

    public ChatEngineSessionState GetState()
    {
        if (_parameters is null)
        {
            throw new InvalidOperationException("Chat session not started.");
        }

        return new ChatEngineSessionState
        {
            Configuration = _parameters.Configuration,
            Agents = _chat.Agents.ToList(),
            Messages = _chat.Messages.ToList(),
            ChatStrategyName = "UnifiedAgentRuntime"
        };
    }

    public async Task DeleteMessageAsync(Guid messageId)
    {
        throw new InvalidOperationException("Messages in an active conversation cannot be edited or deleted. Start a new chat instead.");
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

        var templateService = agentTemplateService ?? throw new InvalidOperationException("Harness agent factory is not configured.");
        var agentFactory = runtimeAgentFactory ?? throw new InvalidOperationException("Harness agent factory is not configured.");
        var template = await templateService.GetByIdAsync(templateId)
            ?? throw new InvalidOperationException($"Saved agent '{request.RuntimeReference.Id}' was not found.");
        if (request.Overrides.McpServerBindings is not null)
        {
            template = template.Clone();
            template.McpServerBindings = request.Overrides.McpServerBindings
                .Select(static binding => binding.Clone())
                .ToList();
        }

        var resolved = ResolvedChatAgentFactory.Resolve(template, request.RuntimeDefaultModel);
        var build = await agentFactory.CreateAsync(new AgentRunRequest
        {
            Agent = resolved.Agent,
            ResolvedModel = resolved.Model,
            Configuration = request.Configuration,
            Conversation = [],
            UserMessage = string.Empty
        }, cancellationToken: cancellationToken);

        _directAgent = build.Agent;
        _directSession = await build.Agent.CreateSessionAsync(cancellationToken);
    }

    private async Task SendDirectAsync(
        string text,
        IReadOnlyList<AppChatMessageFile> files,
        CancellationToken cancellationToken)
    {
        var userMessage = new AppChatMessage(text, DateTime.Now, AppChatRole.User, files: files);
        await AddMessageAsync(userMessage);
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        UpdateAnsweringState(true);
        const string messageId = "direct-harness-response";
        var stream = await GetOrCreateStreamAsync(messageId, _chat.Agents.FirstOrDefault()?.AgentName ?? "Agent");

        try
        {
            await foreach (var update in _directAgent!.RunStreamingAsync(
                               [new ChatMessage(ChatRole.User, text)],
                               _directSession,
                               null,
                               _cancellationTokenSource.Token))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    streamingBridge.Append(stream, update.Text);
                    await (MessageUpdated?.Invoke(stream, false) ?? Task.CompletedTask);
                }
            }

            var final = streamingBridge.Complete(stream, "HarnessAgent");
            ReplaceMessage(stream, final);
            await (MessageUpdated?.Invoke(final, true) ?? Task.CompletedTask);
            _activeStreamsByRuntimeMessageId.Remove(messageId);
        }
        catch (OperationCanceledException)
        {
            await CancelActiveStreamsAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Harness direct chat run failed.");
            await CancelActiveStreamsAsync();
            await AddMessageAsync(new AppChatMessage($"Agent runtime error: {ex.Message}", DateTime.Now, AppChatRole.Assistant));
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            UpdateAnsweringState(false);
        }
    }

    private async Task ApplyRunEventAsync(
        AgentRunEvent runEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (runEvent)
        {
            case AgentTextDelta delta:
                var stream = await GetOrCreateStreamAsync(delta.MessageId, delta.Author);
                streamingBridge.Append(stream, delta.Text);
                await (MessageUpdated?.Invoke(stream, false) ?? Task.CompletedTask);
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
            if (string.IsNullOrWhiteSpace(pair.Value.Content))
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
