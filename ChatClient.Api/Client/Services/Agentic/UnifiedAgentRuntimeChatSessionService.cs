using System.Collections.ObjectModel;
using System.Text;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class UnifiedAgentRuntimeChatSessionService(
    IAgentRunner agentRunner,
    IChatEngineStreamingBridge streamingBridge,
    ILogger<UnifiedAgentRuntimeChatSessionService> logger) : IChatEngineSessionService
{
    private readonly AppChat _chat = new();
    private readonly Dictionary<string, StreamingAppChatMessage> _activeStreamsByRuntimeMessageId =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedRuntimeMessageIds = new(StringComparer.Ordinal);
    private ChatEngineSessionStartRequest? _parameters;
    private CancellationTokenSource? _cancellationTokenSource;

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

        _parameters = request;
        _chat.Reset();
        ClearRunLocalState();
        _chat.SetAgents(request.Agents.Select(static agent => agent.Agent.Clone()));
        ChatReset?.Invoke();

        foreach (var message in request.History.OrderBy(static message => message.MsgDateTime))
        {
            _chat.Messages.Add(message);
            await (MessageAdded?.Invoke(message) ?? Task.CompletedTask);
        }

        await Task.CompletedTask;
    }

    public void ResetChat()
    {
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
                Attachments = (files ?? [])
                    .Select(ToAgentInputAttachment)
                    .ToList()
            };

            var creationContext = new AgentRuntimeCreationContext
            {
                Configuration = _parameters.Configuration,
                DefaultModel = _parameters.RuntimeDefaultModel ?? _parameters.Agents.FirstOrDefault()?.Model
            };

            await foreach (var runEvent in agentRunner.RunAsync(
                               _parameters.RuntimeReference,
                               runtimeRequest,
                               creationContext,
                               new AgentRunContext
                               {
                                   RunId = Guid.NewGuid().ToString("N"),
                                   ConversationId = _chat.Id.ToString("N")
                               },
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
        if (IsAnswering)
        {
            return;
        }

        var message = _chat.Messages.FirstOrDefault(candidate => candidate.Id == messageId);
        if (message is null)
        {
            return;
        }

        _chat.Messages.Remove(message);
        await (MessageDeleted?.Invoke(messageId) ?? Task.CompletedTask);
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
        if (_activeStreamsByRuntimeMessageId.TryGetValue(runtimeMessageId, out var stream) &&
            string.Equals(stream.Content, output.Content, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(output.Author))
            {
                stream.SetAgentName(output.Author);
            }

            var final = streamingBridge.Complete(stream, "unified agent runtime");
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
