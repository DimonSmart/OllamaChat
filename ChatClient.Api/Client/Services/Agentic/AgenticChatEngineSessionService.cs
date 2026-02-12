using ChatClient.Application.Services.Agentic;
using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using ChatClient.Domain.Models.ChatStrategies;
using Microsoft.Extensions.AI;
using OllamaSharp.Models.Exceptions;
using System.Collections.ObjectModel;

namespace ChatClient.Api.Client.Services.Agentic;

public sealed class AgenticChatEngineSessionService(
    ILogger<AgenticChatEngineSessionService> logger,
    IModelCapabilityService modelCapabilityService,
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

        await ValidateResolvedAgentsAsync(request.Agents, cancellationToken);

        _parameters = request;
        _chat.Reset();
        _activeStreams.Clear();
        _chat.SetAgents(request.Agents.Select(CreateRuntimeAgentDescription));
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

        var userMessage = new AppChatMessage(text, DateTime.Now, ChatRole.User, files: files);
        await AddMessageAsync(userMessage);

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        UpdateAnsweringState(true);

        try
        {
            foreach (var (resolvedAgent, round) in BuildExecutionOrder(parameters).WithIndex())
            {
                _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                logger.LogDebug(
                    "Agentic round {RoundNumber}: executing agent {AgentName}",
                    round + 1,
                    resolvedAgent.Agent.AgentName);

                bool shouldContinue = await StreamAgentResponseAsync(
                    resolvedAgent,
                    parameters,
                    text,
                    files,
                    enableRagContext: round == 0,
                    _cancellationTokenSource.Token);
                if (!shouldContinue)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        { }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            UpdateAnsweringState(false);
        }
    }

    private async Task<bool> StreamAgentResponseAsync(
        ResolvedChatAgent resolvedAgent,
        ChatEngineSessionStartRequest parameters,
        string userMessageText,
        IReadOnlyList<AppChatMessageFile> files,
        bool enableRagContext,
        CancellationToken cancellationToken)
    {
        var stream = streamingBridge.Create(resolvedAgent.Agent.AgentName);
        _activeStreams[stream.Id] = stream;
        await AddMessageAsync(stream);

        var startedAt = DateTime.Now;

        try
        {
            var history = historyBuilder.Build(_chat.Messages);
            var request = new ChatEngineOrchestrationRequest
            {
                Agent = resolvedAgent.Agent,
                ResolvedModel = resolvedAgent.Model,
                Configuration = parameters.Configuration,
                Messages = history,
                UserMessage = userMessageText,
                Files = files,
                EnableRagContext = enableRagContext,
                Whiteboard = _chat.Whiteboard
            };

            IReadOnlyList<FunctionCallRecord> functionCalls = [];
            string? retrievedContext = null;

            await foreach (var chunk in orchestrator.StreamAsync(request, cancellationToken))
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
                    await HandleError(chunk.Content, chunk.AgentName);
                    return false;
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
                BuildStatistics(startedAt, resolvedAgent.Model.ModelName, stream.ApproximateTokenCount));
            if (functionCalls.Count > 0)
            {
                final.FunctionCalls = functionCalls;
            }

            ReplaceMessage(stream, final);
            await (MessageUpdated?.Invoke(final, true) ?? Task.CompletedTask);
            return true;
        }
        catch (OperationCanceledException)
        {
            await CancelStreamAsync(stream);
            throw;
        }
        catch (ModelDoesNotSupportToolsException ex)
        {
            logger.LogWarning(ex, "Model does not support tools for agent {AgentName}", resolvedAgent.Agent.AgentName);
            await CancelStreamAsync(stream);
            await HandleError($"The model **{resolvedAgent.Model.ModelName}** does not support function calling.", resolvedAgent.Agent.AgentName);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agentic session failed for agent {AgentName}", resolvedAgent.Agent.AgentName);
            await CancelStreamAsync(stream);
            await HandleError($"An error occurred while getting the response: {ex.Message}", resolvedAgent.Agent.AgentName);
            return false;
        }
        finally
        {
            _activeStreams.Remove(stream.Id);
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

    private async Task HandleError(string text, string? agentName = null)
    {
        await AddMessageAsync(new AppChatMessage(text, DateTime.Now, ChatRole.Assistant, agentName: agentName));
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
        }
    }

    private static AgentDescription CreateRuntimeAgentDescription(ResolvedChatAgent resolvedAgent)
    {
        var source = resolvedAgent.Agent;
        var model = resolvedAgent.Model;

        return new AgentDescription
        {
            Id = source.Id,
            AgentName = source.AgentName,
            Content = source.Content,
            ShortName = source.ShortName,
            ModelName = model.ModelName,
            LlmId = model.ServerId,
            Temperature = source.Temperature,
            RepeatPenalty = source.RepeatPenalty,
            FunctionSettings = new FunctionSettings
            {
                AutoSelectCount = source.FunctionSettings.AutoSelectCount,
                SelectedFunctions = [.. source.FunctionSettings.SelectedFunctions]
            },
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };
    }

    private static string BuildStatistics(DateTime startedAt, string modelName, int tokenCount)
    {
        var duration = DateTime.Now - startedAt;
        var tokensPerSecond = duration.TotalSeconds > 0
            ? (tokenCount / duration.TotalSeconds).ToString("F1")
            : "N/A";

        return $"time {duration.TotalSeconds:F1}s | model {modelName} | tokens {tokenCount} ({tokensPerSecond}/s)";
    }

    private static IReadOnlyList<ResolvedChatAgent> BuildExecutionOrder(ChatEngineSessionStartRequest parameters)
    {
        int rounds = parameters.ChatStrategyOptions switch
        {
            RoundRobinChatStrategyOptions roundRobin => Math.Max(1, roundRobin.Rounds),
            RoundRobinSummaryChatStrategyOptions summary => Math.Max(1, summary.Rounds),
            _ => 1
        };

        if (parameters.ChatStrategyOptions is RoundRobinSummaryChatStrategyOptions summaryOptions)
        {
            var summaryAgent = ResolveSummaryAgent(parameters.Agents, summaryOptions.SummaryAgent);
            if (summaryAgent is null)
            {
                return BuildRoundRobinOrder(parameters.Agents, rounds);
            }

            var roundAgents = parameters.Agents
                .Where(agent => !IsSameAgent(agent, summaryAgent))
                .ToList();

            if (roundAgents.Count == 0)
            {
                return [summaryAgent];
            }

            var ordered = new List<ResolvedChatAgent>(roundAgents.Count * rounds + 1);
            for (int round = 0; round < rounds; round++)
            {
                ordered.AddRange(roundAgents);
            }

            ordered.Add(summaryAgent);
            return ordered;
        }

        return BuildRoundRobinOrder(parameters.Agents, rounds);
    }

    private static IReadOnlyList<ResolvedChatAgent> BuildRoundRobinOrder(
        IReadOnlyList<ResolvedChatAgent> agents,
        int rounds)
    {
        if (rounds == 1)
        {
            return agents;
        }

        var orderedAgents = new List<ResolvedChatAgent>(agents.Count * rounds);
        for (int round = 0; round < rounds; round++)
        {
            orderedAgents.AddRange(agents);
        }

        return orderedAgents;
    }

    private static ResolvedChatAgent? ResolveSummaryAgent(
        IReadOnlyList<ResolvedChatAgent> agents,
        string summaryAgentId)
    {
        if (string.IsNullOrWhiteSpace(summaryAgentId))
        {
            return null;
        }

        return agents.FirstOrDefault(agent =>
            string.Equals(agent.Agent.AgentId, summaryAgentId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSameAgent(ResolvedChatAgent left, ResolvedChatAgent right) =>
        string.Equals(left.Agent.AgentId, right.Agent.AgentId, StringComparison.OrdinalIgnoreCase);
}

file static class EnumerableExtensions
{
    public static IEnumerable<(T Item, int Index)> WithIndex<T>(this IReadOnlyList<T> items)
    {
        for (int index = 0; index < items.Count; index++)
        {
            yield return (items[index], index);
        }
    }
}
