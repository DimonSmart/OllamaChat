using System.Collections.ObjectModel;
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
    private readonly Dictionary<Guid, StreamingAppChatMessage> _activeStreams = [];
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
        _activeStreams.Clear();
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
        _activeStreams.Clear();
        _parameters = null;
        ChatReset?.Invoke();
    }

    public async Task CancelAsync()
    {
        _cancellationTokenSource?.Cancel();

        foreach (var stream in _activeStreams.Values.ToList())
        {
            var canceled = streamingBridge.Cancel(stream);
            ReplaceMessage(stream, canceled);
            await (MessageUpdated?.Invoke(canceled, true) ?? Task.CompletedTask);
        }

        _activeStreams.Clear();
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

        var stream = streamingBridge.Create(
            _parameters.RuntimeReference.Id,
            ResolveRuntimeDisplayName(_parameters));
        _activeStreams[stream.Id] = stream;
        await AddMessageAsync(stream);

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        UpdateAnsweringState(true);

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
                await ApplyRunEventAsync(stream, runEvent, _cancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
            var canceled = streamingBridge.Cancel(stream);
            ReplaceMessage(stream, canceled);
            await (MessageUpdated?.Invoke(canceled, true) ?? Task.CompletedTask);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unified agent chat run failed.");
            await AddMessageAsync(new AppChatMessage(
                $"An error occurred while getting the response: {ex.Message}",
                DateTime.Now,
                AppChatRole.Assistant));
        }
        finally
        {
            _activeStreams.Remove(stream.Id);
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
        StreamingAppChatMessage stream,
        AgentRunEvent runEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (runEvent)
        {
            case AgentTextDelta delta:
                streamingBridge.Append(stream, delta.Text);
                await (MessageUpdated?.Invoke(stream, false) ?? Task.CompletedTask);
                break;

            case AgentMessageCompleted completed:
                await CompleteOrAddAssistantMessageAsync(stream, completed.Message);
                break;

            case AgentRunCompleted:
                if (_activeStreams.ContainsKey(stream.Id) && !string.IsNullOrWhiteSpace(stream.Content))
                {
                    var final = streamingBridge.Complete(stream, "unified agent runtime");
                    ReplaceMessage(stream, final);
                    await (MessageUpdated?.Invoke(final, true) ?? Task.CompletedTask);
                    _activeStreams.Remove(stream.Id);
                }
                break;

            case AgentRunFailed failed:
                await AddFailureAsync(stream, failed.Error);
                break;
        }
    }

    private async Task CompleteOrAddAssistantMessageAsync(
        StreamingAppChatMessage stream,
        AgentOutputMessage output)
    {
        if (_activeStreams.ContainsKey(stream.Id) &&
            string.Equals(stream.Content, output.Content, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(output.Author))
            {
                stream.SetAgentName(output.Author);
            }

            var final = streamingBridge.Complete(stream, "unified agent runtime");
            ReplaceMessage(stream, final);
            await (MessageUpdated?.Invoke(final, true) ?? Task.CompletedTask);
            _activeStreams.Remove(stream.Id);
            return;
        }

        await AddMessageAsync(new AppChatMessage(
            output.Content,
            DateTime.Now,
            AppChatRole.Assistant,
            agentId: output.Author,
            agentName: output.Author));
    }

    private async Task AddFailureAsync(
        StreamingAppChatMessage stream,
        AgentRunError error)
    {
        if (_activeStreams.ContainsKey(stream.Id))
        {
            var canceled = streamingBridge.Cancel(stream);
            ReplaceMessage(stream, canceled);
            await (MessageUpdated?.Invoke(canceled, true) ?? Task.CompletedTask);
            _activeStreams.Remove(stream.Id);
        }

        await AddMessageAsync(new AppChatMessage(
            error.Message,
            DateTime.Now,
            AppChatRole.Assistant));
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
}
