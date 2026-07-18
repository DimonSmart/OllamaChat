using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using System.Collections.ObjectModel;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class OrchestrationWorkflowChatSessionService(
    IHeadlessWorkflowRunner headlessWorkflowRunner,
    IChatEngineStreamingBridge streamingBridge,
    ILogger<OrchestrationWorkflowChatSessionService> logger) : IOrchestrationWorkflowSessionService
{
    private readonly AppChat _chat = new();
    private readonly Dictionary<string, StreamingAppChatMessage> _activeStreamsByRuntimeMessageId =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedRuntimeMessageIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cancellationTokenSource;
    private OrchestrationWorkflowSessionStartRequest? _parameters;
    private IHeadlessWorkflowSession? _headlessSession;

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
        ArgumentNullException.ThrowIfNull(request);

        await DisposeHeadlessSessionAsync();
        _headlessSession = await headlessWorkflowRunner.StartAsync(
            new HeadlessWorkflowSessionStartRequest
            {
                Workflow = request.Workflow,
                Participants = request.Participants,
                Agents = request.Agents,
                Configuration = request.Configuration,
                StartInputs = request.StartInputs,
                SessionTitle = request.SessionTitle ?? request.Workflow.DisplayName,
                SessionDescription = request.SessionDescription ?? string.Empty
            },
            cancellationToken);

        _parameters = request;
        TaskSessionId = _headlessSession.TaskSessionId;
        ClearChatState();
        _chat.SetAgents(request.Agents.Count > 0
            ? request.Agents.Select(static agent => agent.Agent.Clone())
            : request.Participants.Select(static participant => new AgentExecutionSpec
            {
                RuntimeAgentId = participant.Id,
                AgentName = participant.DisplayName,
                Summary = participant.Summary
            }));
        ChatReset?.Invoke();
    }

    public void ResetChat()
    {
        DisposeHeadlessSessionAsync().AsTask().GetAwaiter().GetResult();
        ClearChatState();
        _parameters = null;
        TaskSessionId = null;
        ChatReset?.Invoke();
    }

    public async Task CancelAsync()
    {
        _cancellationTokenSource?.Cancel();
        await CancelActiveStreamsAsync();
        ClearRunLocalState();
        UpdateAnsweringState(false);
    }

    public Task SendAsync(
        string text,
        IReadOnlyList<AppChatMessageFile>? files = null,
        CancellationToken cancellationToken = default)
    {
        if (_parameters is null)
        {
            throw new InvalidOperationException("Workflow session not started.");
        }

        return ExecuteWorkflowAsync(text, files ?? [], includeUserMessage: true, cancellationToken);
    }

    public Task KickoffAsync(CancellationToken cancellationToken = default)
    {
        if (_parameters is null)
        {
            throw new InvalidOperationException("Workflow session not started.");
        }

        return ExecuteWorkflowAsync(null, [], includeUserMessage: false, cancellationToken);
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

        var message = _chat.Messages.FirstOrDefault(candidate => candidate.Id == messageId);
        if (message is null)
        {
            return;
        }

        _chat.Messages.Remove(message);
        await (MessageDeleted?.Invoke(messageId) ?? Task.CompletedTask);
    }

    private async Task ExecuteWorkflowAsync(
        string? text,
        IReadOnlyList<AppChatMessageFile> files,
        bool includeUserMessage,
        CancellationToken cancellationToken)
    {
        if (IsAnswering || _parameters is null || _headlessSession is null)
        {
            return;
        }

        if (includeUserMessage)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            await AddMessageAsync(new AppChatMessage(text, DateTime.Now, AppChatRole.User, files: files));
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        UpdateAnsweringState(true);

        try
        {
            var request = new HeadlessWorkflowTurnRequest
            {
                UserMessage = includeUserMessage ? text : null,
                UserFiles = files
            };

            await foreach (var runEvent in _headlessSession.RunTurnAsync(
                               request,
                               _cancellationTokenSource.Token))
            {
                await ApplyRunEventAsync(runEvent, _cancellationTokenSource.Token);
            }

            await CompleteRemainingStreamsAsync();
        }
        catch (OperationCanceledException)
        {
            await CancelActiveStreamsAsync();
        }
        catch (WorkflowAssistantErrorException ex)
        {
            await CancelActiveStreamsAsync();
            await AddMessageAsync(new AppChatMessage(ex.Message, DateTime.Now, AppChatRole.Assistant));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Workflow chat run failed.");
            await CancelActiveStreamsAsync();
            await AddMessageAsync(new AppChatMessage(
                "Workflow runtime error: The run failed before a terminal result was produced.",
                DateTime.Now,
                AppChatRole.Assistant));
        }
        finally
        {
            ClearRunLocalState();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            UpdateAnsweringState(false);
        }
    }

    private async Task ApplyRunEventAsync(
        HeadlessWorkflowEvent runEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (runEvent)
        {
            case HeadlessWorkflowStarted started:
                TaskSessionId = started.TaskSessionId;
                break;

            case HeadlessWorkflowTextDelta delta:
                var stream = await GetOrCreateStreamAsync(delta.MessageId, delta.Author);
                streamingBridge.Append(stream, delta.Text);
                await (MessageUpdated?.Invoke(stream, false) ?? Task.CompletedTask);
                break;

            case HeadlessWorkflowMessageCompleted completed:
                await CompleteOrAddAssistantMessageAsync(
                    completed.MessageId,
                    completed.ParticipantId,
                    completed.Author,
                    completed.Content);
                break;

            case HeadlessWorkflowCompleted completed:
                await CompleteOrAddAssistantMessageAsync(
                    completed.Result.FinalMessageId,
                    completed.Result.Metadata.GetValueOrDefault("finalParticipantId", completed.Result.FinalAuthor),
                    completed.Result.FinalAuthor,
                    completed.Result.FinalContent);
                await CompleteRemainingStreamsAsync();
                break;
        }
    }

    private async Task CompleteOrAddAssistantMessageAsync(
        string runtimeMessageId,
        string? participantId,
        string author,
        string content)
    {
        if (_completedRuntimeMessageIds.Contains(runtimeMessageId))
        {
            return;
        }

        if (_activeStreamsByRuntimeMessageId.TryGetValue(runtimeMessageId, out var stream))
        {
            if (!string.IsNullOrWhiteSpace(participantId))
            {
                stream.SetAgentId(participantId);
            }

            if (!string.IsNullOrWhiteSpace(author))
            {
                stream.SetAgentName(author);
            }

            var final = streamingBridge.Complete(stream, content, "workflow runtime");
            ReplaceMessage(stream, final);
            await (MessageUpdated?.Invoke(final, true) ?? Task.CompletedTask);
            _activeStreamsByRuntimeMessageId.Remove(runtimeMessageId);
            _completedRuntimeMessageIds.Add(runtimeMessageId);
            return;
        }

        await AddMessageAsync(new AppChatMessage(
            content,
            DateTime.Now,
            AppChatRole.Assistant,
            agentId: participantId,
            agentName: author));
        _completedRuntimeMessageIds.Add(runtimeMessageId);
    }

    private async Task<StreamingAppChatMessage> GetOrCreateStreamAsync(
        string runtimeMessageId,
        string author)
    {
        if (_activeStreamsByRuntimeMessageId.TryGetValue(runtimeMessageId, out var existing))
        {
            if (!string.IsNullOrWhiteSpace(author))
            {
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

            var final = streamingBridge.Complete(pair.Value, "workflow runtime");
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

    private void ClearChatState()
    {
        _chat.Reset();
        ClearRunLocalState();
    }

    private void ClearRunLocalState()
    {
        _activeStreamsByRuntimeMessageId.Clear();
        _completedRuntimeMessageIds.Clear();
    }

    private async ValueTask DisposeHeadlessSessionAsync()
    {
        if (_headlessSession is null)
        {
            return;
        }

        await _headlessSession.DisposeAsync();
        _headlessSession = null;
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
}
