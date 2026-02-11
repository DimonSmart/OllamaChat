using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;
using OllamaSharp.Models.Exceptions;
using System.Collections.ObjectModel;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class AgenticChatEngineSessionService(
    ILogger<AgenticChatEngineSessionService> logger,
    IChatEngineOrchestrator orchestrator,
    IChatEngineHistoryBuilder historyBuilder,
    IChatEngineStreamingBridge streamingBridge) : IChatEngineSessionService
{
    private readonly AppChat _chat = new();
    private readonly Dictionary<Guid, StreamingAppChatMessage> _activeStreams = [];
    private CancellationTokenSource? _cancellationTokenSource;
    private ChatEngineSessionStartRequest? _parameters;

    public event Action<bool>? AnsweringStateChanged;
    public event Action? ChatReset;
    public event Func<IAppChatMessage, Task>? MessageAdded;
    public event Func<IAppChatMessage, bool, Task>? MessageUpdated;
    public event Func<Guid, Task>? MessageDeleted;

    public bool IsAnswering { get; private set; }

    public Guid Id => _chat.Id;

    public IReadOnlyCollection<AgentDescription> AgentDescriptions => _chat.AgentDescriptions;

    public ObservableCollection<IAppChatMessage> Messages => _chat.Messages;
    IReadOnlyCollection<IAppChatMessage> IChatEngineSessionService.Messages => _chat.Messages;

    public async Task StartAsync(ChatEngineSessionStartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Agents.Count == 0)
            throw new ArgumentException("At least one agent must be provided.", nameof(request));

        _parameters = request;
        _chat.Reset();
        _activeStreams.Clear();
        _chat.SetAgents(request.Agents);
        ChatReset?.Invoke();

        if (request.History.Count > 0)
            await LoadHistoryAsync(request.History);
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

        if (_activeStreams.Count > 0)
        {
            foreach (var stream in _activeStreams.Values.ToList())
            {
                var canceled = streamingBridge.Cancel(stream);
                ReplaceMessage(stream, canceled);
                await (MessageUpdated?.Invoke(canceled, true) ?? Task.CompletedTask);
            }

            _activeStreams.Clear();
        }

        UpdateAnsweringState(false);
    }

    public Task SendAsync(string text, IReadOnlyList<AppChatMessageFile>? files = null, CancellationToken cancellationToken = default)
    {
        if (_parameters is null)
            throw new InvalidOperationException("Chat session not started.");

        return GenerateAnswerAsync(text, _parameters, files ?? [], cancellationToken);
    }

    public ChatEngineSessionState GetState()
    {
        if (_parameters is null)
            throw new InvalidOperationException("Chat session not started.");

        return new ChatEngineSessionState
        {
            Configuration = _parameters.Configuration,
            Agents = _chat.AgentDescriptions.ToList(),
            Messages = _chat.Messages.ToList(),
            ChatStrategyName = _parameters.ChatStrategyName
        };
    }

    public async Task DeleteMessageAsync(Guid messageId)
    {
        if (IsAnswering)
            return;

        var message = _chat.Messages.FirstOrDefault(m => m.Id == messageId);
        if (message is null)
            return;

        _chat.Messages.Remove(message);
        await (MessageDeleted?.Invoke(messageId) ?? Task.CompletedTask);
    }

    private async Task GenerateAnswerAsync(
        string text,
        ChatEngineSessionStartRequest parameters,
        IReadOnlyList<AppChatMessageFile> files,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || IsAnswering)
            return;

        var primaryAgent = parameters.Agents[0];
        if (parameters.Agents.Count > 1)
        {
            logger.LogInformation(
                "Agentic session is currently single-agent. Using the first agent: {AgentName}",
                primaryAgent.AgentName);
        }

        var userMessage = new AppChatMessage(text, DateTime.Now, ChatRole.User, files: files);
        await AddMessageAsync(userMessage);

        var stream = streamingBridge.Create(primaryAgent.AgentName);
        _activeStreams[stream.Id] = stream;
        await AddMessageAsync(stream);

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var startedAt = DateTime.Now;
        UpdateAnsweringState(true);

        try
        {
            var history = historyBuilder.Build(_chat.Messages);
            var request = new ChatEngineOrchestrationRequest
            {
                Agent = primaryAgent,
                Configuration = parameters.Configuration,
                Messages = history,
                UserMessage = text,
                Files = files
            };

            IReadOnlyList<FunctionCallRecord> functionCalls = [];
            string? retrievedContext = null;

            await foreach (var chunk in orchestrator.StreamAsync(request, _cancellationTokenSource.Token))
            {
                if (!string.IsNullOrWhiteSpace(chunk.RetrievedContext))
                {
                    retrievedContext = chunk.RetrievedContext;
                }

                if (chunk.FunctionCalls is { Count: > 0 })
                {
                    functionCalls = chunk.FunctionCalls;
                }

                if (chunk.IsError)
                {
                    await AddRetrievedContextMessageIfNeededAsync(retrievedContext);
                    await CancelStreamAsync(stream);
                    await HandleError(chunk.Content);
                    return;
                }

                if (!string.IsNullOrEmpty(chunk.Content))
                {
                    if (!string.IsNullOrWhiteSpace(chunk.AgentName) &&
                        !string.Equals(chunk.AgentName, stream.AgentName, StringComparison.Ordinal))
                    {
                        stream.SetAgentName(chunk.AgentName);
                    }

                    streamingBridge.Append(stream, chunk.Content);
                    await (MessageUpdated?.Invoke(stream, false) ?? Task.CompletedTask);
                }

                if (chunk.IsFinal)
                {
                    break;
                }
            }

            await AddRetrievedContextMessageIfNeededAsync(retrievedContext);
            var final = streamingBridge.Complete(
                stream,
                BuildStatistics(startedAt, primaryAgent.ModelName ?? parameters.Configuration.ModelName, stream.ApproximateTokenCount));
            if (functionCalls.Count > 0)
            {
                final.FunctionCalls = functionCalls;
            }
            ReplaceMessage(stream, final);
            await (MessageUpdated?.Invoke(final, true) ?? Task.CompletedTask);
        }
        catch (OperationCanceledException)
        {
            await CancelStreamAsync(stream);
        }
        catch (ModelDoesNotSupportToolsException ex)
        {
            logger.LogWarning(ex, "Model does not support tools for agent {AgentName}", primaryAgent.AgentName);
            await CancelStreamAsync(stream);
            await HandleError($"The model **{primaryAgent.ModelName ?? parameters.Configuration.ModelName}** does not support function calling.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agentic session failed");
            await CancelStreamAsync(stream);
            await HandleError($"An error occurred while getting the response: {ex.Message}");
        }
        finally
        {
            _activeStreams.Remove(stream.Id);
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            UpdateAnsweringState(false);
        }
    }

    private async Task AddMessageAsync(IAppChatMessage message)
    {
        if (_chat.Messages.Any(m => m.Id == message.Id))
            return;

        _chat.Messages.Add(message);
        await (MessageAdded?.Invoke(message) ?? Task.CompletedTask);
    }

    private async Task LoadHistoryAsync(IEnumerable<IAppChatMessage> messages)
    {
        foreach (var message in messages.OrderBy(m => m.MsgDateTime))
        {
            if (_chat.Messages.Any(m => m.Id == message.Id))
                continue;

            _chat.Messages.Add(message);
            await (MessageAdded?.Invoke(message) ?? Task.CompletedTask);
        }
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

    private async Task CancelStreamAsync(StreamingAppChatMessage stream)
    {
        if (!_activeStreams.ContainsKey(stream.Id))
            return;

        var canceled = streamingBridge.Cancel(stream);
        ReplaceMessage(stream, canceled);
        await (MessageUpdated?.Invoke(canceled, true) ?? Task.CompletedTask);
        _activeStreams.Remove(stream.Id);
    }

    private async Task HandleError(string text)
    {
        await AddMessageAsync(new AppChatMessage(text, DateTime.Now, ChatRole.Assistant));
    }

    private async Task AddRetrievedContextMessageIfNeededAsync(string? retrievedContext)
    {
        if (string.IsNullOrWhiteSpace(retrievedContext))
            return;

        bool alreadyExists = _chat.Messages.Any(m =>
            m.Role == ChatRole.Tool &&
            string.Equals(m.Content, retrievedContext, StringComparison.Ordinal));

        if (alreadyExists)
            return;

        await AddMessageAsync(new AppChatMessage(retrievedContext, DateTime.Now, ChatRole.Tool));
    }

    private void UpdateAnsweringState(bool isAnswering)
    {
        IsAnswering = isAnswering;
        AnsweringStateChanged?.Invoke(isAnswering);
    }

    private static string BuildStatistics(DateTime startedAt, string modelName, int tokenCount)
    {
        var duration = DateTime.Now - startedAt;
        var tokensPerSecond = duration.TotalSeconds > 0
            ? (tokenCount / duration.TotalSeconds).ToString("F1")
            : "N/A";

        return $"time {duration.TotalSeconds:F1}s | model {modelName} | tokens {tokenCount} ({tokensPerSecond}/s)";
    }
}
