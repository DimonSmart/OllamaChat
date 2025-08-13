using System.Collections.ObjectModel;

using ChatClient.Api.Services;
using ChatClient.Shared.Models;

using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;

using OllamaSharp.Models.Exceptions;
#pragma warning disable SKEXP0110

namespace ChatClient.Api.Client.Services;

public class ChatService(
    KernelService kernelService,
    ILogger<ChatService> logger) : IChatService
{
    private CancellationTokenSource? _cancellationTokenSource;
    private StreamingMessageManager _streamingManager = null!;
    private readonly Dictionary<string, StreamingAppChatMessage> _activeStreams = new();
    private const string PlaceholderAgent = "__placeholder__";
    private List<AgentDescription> _agentDescriptions = [];

   // private readonly Dictionary<string, FunctionCallRecordingFilter> _trackingFilters = new();

    public event Action<bool>? AnsweringStateChanged;
    public event Action? ChatReset;
    public event Func<IAppChatMessage, Task>? MessageAdded;
    public event Func<IAppChatMessage, bool, Task>? MessageUpdated;
    public event Func<Guid, Task>? MessageDeleted;

    public bool IsAnswering { get; private set; }
    public ObservableCollection<IAppChatMessage> Messages { get; } = [];

    private TrackingFiltersScope CreateTrackingScope()
    {
        ClearTrackingFilters();
        return new TrackingFiltersScope(ClearTrackingFilters);
    }

    public IReadOnlyList<AgentDescription> AgentDescriptions => _agentDescriptions;

    public void InitializeChat(IEnumerable<AgentDescription> initialAgents)
    {
        if (initialAgents is null)
            throw new ArgumentNullException(nameof(initialAgents));

        var agentsList = initialAgents.ToList();
        if (agentsList.Count == 0)
            throw new ArgumentException("At least one agent must be selected.", nameof(initialAgents));

        Messages.Clear();
        _activeStreams.Clear();
        _streamingManager = new StreamingMessageManager();
        _agentDescriptions = agentsList;

        AddSystemMessages();
        ChatReset?.Invoke();
    }

    private void AddSystemMessages()
    {
        foreach (var agent in _agentDescriptions)
        {
            var systemMessage = new AppChatMessage(agent.Content, DateTime.Now, ChatRole.System, string.Empty);
            Messages.Add(systemMessage);
        }
    }

    public void ResetChat()
    {
        Messages.Clear();
        _agentDescriptions.Clear();
        _activeStreams.Clear();
        ChatReset?.Invoke();
    }

    public async Task CancelAsync()
    {
        _cancellationTokenSource?.Cancel();

        if (_streamingManager != null && _activeStreams.Any())
        {
            await CancelActiveStreams();
        }

        UpdateAnsweringState(false);
    }

    private async Task CancelActiveStreams()
    {
        foreach (var message in _activeStreams.Values.ToList())
        {
            AppChatMessage canceledMessage = _streamingManager.CancelStreaming(message);
            await ReplaceStreamingMessageWithFinal(message, canceledMessage);
        }
        _activeStreams.Clear();
    }

    public async Task GenerateAnswerAsync(string text, ChatConfiguration chatConfiguration, IReadOnlyList<ChatMessageFile>? files = null)
    {
        if (string.IsNullOrWhiteSpace(text) || IsAnswering)
            return;

        using var trackingScope = CreateTrackingScope();

        await AddMessageAsync(new AppChatMessage(text, DateTime.Now, ChatRole.User, string.Empty, files));

        await AddMessageAsync(_activeStreams[PlaceholderAgent] = CreateInitialPlaceholderMessage());

        UpdateAnsweringState(true);

        _cancellationTokenSource = new CancellationTokenSource();
        try
        {
            await ProcessWithRuntime(chatConfiguration, text, _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (ModelDoesNotSupportToolsException ex)
        {
            await HandleModelNotSupportingTools(ex, chatConfiguration);
        }
        catch (Exception ex)
        {
            await HandleError($"An error occurred while getting the response: {ex.Message}");
        }
        finally
        {
            CleanupWithoutTrackingFilters();
            await FinalizeProcessing(chatConfiguration);
        }
    }

    private StreamingAppChatMessage CreateInitialPlaceholderMessage()
    {
        // Display a temporary placeholder while waiting for the first agent token
        var defaultShortName = _agentDescriptions.FirstOrDefault()?.ShortName
            ?? _agentDescriptions.FirstOrDefault()?.AgentName
            ?? string.Empty;
        var placeholder = _streamingManager.CreateStreamingMessage(agentName: defaultShortName);
        placeholder.Append("...");
        return placeholder;
    }

    private async Task AddMessageAsync(IAppChatMessage message)
    {
        Messages.Add(message);
        await (MessageAdded?.Invoke(message) ?? Task.CompletedTask);
    }

    private async Task ProcessWithRuntime(ChatConfiguration chatConfiguration, string userMessage, CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing response with configuration: {chatConfiguration}", chatConfiguration);
        var runtime = new InProcessRuntime();
        await runtime.StartAsync(cancellationToken);

        var agents = await CreateAgents(chatConfiguration, userMessage);
        var groupChatManager = CreateGroupChatManager(chatConfiguration);
        var chatOrchestration = CreateChatOrchestration(groupChatManager, agents, chatConfiguration);

        try
        {
            var invokeResult = await chatOrchestration.InvokeAsync(userMessage, runtime, cancellationToken);
            var xxx = await invokeResult.GetValueAsync(cancellationToken: cancellationToken);

        }
        catch (Exception ex)
        {
            logger.LogError(ex.Message);
        }

        finally
        {
            RemoveTrackingFilters(agents);
        }
    }

    private async Task<List<ChatCompletionAgent>> CreateAgents(ChatConfiguration chatConfiguration, string userMessage)
    {
        var agents = new List<ChatCompletionAgent>();

        foreach (var desc in _agentDescriptions)
        {
            var agentKernel = await kernelService.CreateKernelAsync(
                chatConfiguration with
                {
                    Functions = desc.FunctionSettings.SelectedFunctions,
                    ModelName = string.IsNullOrWhiteSpace(desc.ModelName)
                        ? chatConfiguration.ModelName
                        : desc.ModelName
                },
                desc.FunctionSettings.AutoSelectCount > 0 ? userMessage : null,
                desc.FunctionSettings.AutoSelectCount > 0 ? desc.FunctionSettings.AutoSelectCount : null);

            var agentName = !string.IsNullOrWhiteSpace(desc.ShortName) ? desc.ShortName : desc.AgentName;

            var trackingFilter = new FunctionCallRecordingFilter();
            agentKernel.FunctionInvocationFilters.Add(trackingFilter);
            _trackingFilters[agentName] = trackingFilter;

            agents.Add(new ChatCompletionAgent
            {
                Name = agentName,
                Description = desc.AgentName,
                Instructions = desc.Content,
                Kernel = agentKernel,
                Arguments = new KernelArguments(new PromptExecutionSettings
                {
                    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: true)
                })
            });
        }

        return agents;
    }

    private RoundRobinGroupChatManager CreateGroupChatManager(ChatConfiguration chatConfiguration)
    {
        return _agentDescriptions.Count == 1
            ? new RoundRobinGroupChatManager() { MaximumInvocationCount = 1 }
            : new RoundRobinGroupChatManager() { MaximumInvocationCount = 1 };
    }

    private GroupChatOrchestration CreateChatOrchestration(
        RoundRobinGroupChatManager groupChatManager,
        List<ChatCompletionAgent> agents,
        ChatConfiguration chatConfiguration)
    {
        return new GroupChatOrchestration(groupChatManager, agents.ToArray())
        {
            // Стриминговый колбэк для инкрементальных токенов
            StreamingResponseCallback = async (streamingContent, isFinal) =>
            {
                var agentName = streamingContent.AuthorName;
                if (string.IsNullOrWhiteSpace(agentName))
                    agentName = _agentDescriptions.FirstOrDefault()?.AgentName ?? "Assistant";

                if (!_activeStreams.TryGetValue(agentName, out var message))
                {
                    if (_activeStreams.TryGetValue(PlaceholderAgent, out var placeholder))
                    {
                        _activeStreams.Remove(PlaceholderAgent);
                        placeholder.SetAgentName(agentName);
                        placeholder.ResetContent();
                        _activeStreams[agentName] = placeholder;
                        message = placeholder;
                    }
                    else
                    {
                        message = await CreateStreamingState(agentName);
                    }
                }

                if (!string.IsNullOrEmpty(streamingContent.Content))
                    await UpdateStreamingMessage(message, streamingContent.Content);

                if (isFinal)
                {
                    await CompleteStreamingMessage(message, chatConfiguration);
                    _activeStreams.Remove(agentName);
                }
            }
        };
    }

    private async Task<StreamingAppChatMessage> CreateStreamingState(
        string agentName)
    {
        var stream = _streamingManager.CreateStreamingMessage(null, agentName);
        if (_trackingFilters.TryGetValue(agentName, out var agentFilter))
            stream.FunctionCallStartIndex = agentFilter.Records.Count;
        _activeStreams[agentName] = stream;

        await AddMessageAsync(stream);
        return stream;
    }

    private async Task UpdateStreamingMessage(
        StreamingAppChatMessage message,
        string content)
    {
        message.Append(content);
        message.ApproximateTokenCount++;

        if (MessageUpdated != null)
            await MessageUpdated(message, false);
    }

    private void RemoveTrackingFilters(List<ChatCompletionAgent> agents)
    {
        foreach (var agent in agents)
        {
            if (!string.IsNullOrEmpty(agent.Name) && _trackingFilters.TryGetValue(agent.Name, out var filter))
                agent.Kernel.FunctionInvocationFilters.Remove(filter);
        }
    }

    private async Task HandleModelNotSupportingTools(ModelDoesNotSupportToolsException ex, ChatConfiguration chatConfiguration)
    {
        logger.LogWarning(ex, "Model {ModelName} does not support function calling", chatConfiguration.ModelName);

        ClearActiveStreams();

        string errorMessage = chatConfiguration.Functions.Any()
            ? $"⚠️ The model **{chatConfiguration.ModelName}** does not support function calling. Please either:\n\n" +
              "• Switch to a model that supports function calling\n" +
              "• Disable all functions for this conversation\n\n" +
              "You can see which models support function calling on the Models page."
            : $"⚠️ The model **{chatConfiguration.ModelName}** does not support the requested functionality.";

        await HandleError(errorMessage);
    }

    private void ClearActiveStreams()
    {
        foreach (var message in _activeStreams.Values.ToList())
        {
            RemoveStreamingMessage(message);
        }
        _activeStreams.Clear();
    }

    private async Task FinalizeProcessing(
        ChatConfiguration chatConfiguration)
    {
        await CompleteActiveStreams(chatConfiguration);
    }

    private async Task CompleteActiveStreams(ChatConfiguration chatConfiguration)
    {
        foreach (var kvp in _activeStreams.ToList())
        {
            await CompleteStreamingMessage(kvp.Value, chatConfiguration);
            _activeStreams.Remove(kvp.Key);
        }
    }

    private async Task CompleteStreamingMessage(
        StreamingAppChatMessage message,
        ChatConfiguration chatConfiguration)
    {
        var messageFunctionCalls = new List<FunctionCallRecord>();
        if (!string.IsNullOrEmpty(message.AgentName) &&
            _trackingFilters.TryGetValue(message.AgentName, out var filter))
        {
            messageFunctionCalls = filter.Records.Skip(message.FunctionCallStartIndex).ToList();
            foreach (var fc in messageFunctionCalls)
            {
                message.AddFunctionCall(fc);
            }
        }

        TimeSpan processingTime = DateTime.Now - message.MsgDateTime;

        var statistics = _streamingManager.BuildStatistics(
            processingTime,
            chatConfiguration,
            message.ApproximateTokenCount,
            messageFunctionCalls.Select(fc => fc.Server).Distinct());

        var finalMessage = _streamingManager.CompleteStreaming(message, statistics);
        await ReplaceStreamingMessageWithFinal(message, finalMessage);
    }



    private async Task ReplaceStreamingMessageWithFinal(StreamingAppChatMessage streamingMessage, AppChatMessage finalMessage)
    {
        int index = Messages.IndexOf(streamingMessage);

        if (index >= 0)
        {
            Messages[index] = finalMessage;
            await (MessageUpdated?.Invoke(finalMessage, true) ?? Task.CompletedTask);
        }
    }

    private void RemoveStreamingMessage(StreamingAppChatMessage streamingMessage)
    {
        Messages.Remove(streamingMessage);
    }

    private async Task HandleError(string text)
    {
        await AddMessageAsync(new AppChatMessage(text, DateTime.Now, ChatRole.Assistant, string.Empty));
    }

    private void ClearTrackingFilters()
    {
        foreach (var filter in _trackingFilters.Values)
            filter.Clear();
        _trackingFilters.Clear();
    }

    private void CleanupWithoutTrackingFilters()
    {
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        UpdateAnsweringState(false);
    }

    private void UpdateAnsweringState(bool isAnswering)
    {
        IsAnswering = isAnswering;
        AnsweringStateChanged?.Invoke(isAnswering);
    }

    public async Task DeleteMessageAsync(Guid id)
    {
        if (IsAnswering)
            return;

        var message = Messages.FirstOrDefault(m => m.Id == id);
        if (message == null)
            return;

        Messages.Remove(message);
        await (MessageDeleted?.Invoke(id) ?? Task.CompletedTask);
    }
}

#pragma warning restore SKEXP0110
