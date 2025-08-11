using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using ChatClient.Api.Services;
using ChatClient.Shared.Models;

using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;

using OllamaSharp.Models.Exceptions;
#pragma warning disable SKEXP0110

namespace ChatClient.Api.Client.Services;

public class ChatService(
    KernelService kernelService,
    ILogger<ChatService> logger) : IChatService
{
    private const int UpdateIntervalMs = 500;

    private CancellationTokenSource? _cancellationTokenSource;
    private StreamingMessageManager _streamingManager = null!;
    private readonly Dictionary<string, StreamState> _activeStreams = new();
    private List<AgentDescription> _agentDescriptions = [];

    private sealed class StreamState
    {
        public StreamingAppChatMessage Message { get; }
        public int ApproximateTokenCount { get; set; }
        public DateTime StartTime { get; set; }
        public int FunctionCallStartIndex { get; set; }

        public StreamState(StreamingAppChatMessage message, int functionCallStartIndex)
        {
            Message = message;
            StartTime = DateTime.Now;
            FunctionCallStartIndex = functionCallStartIndex;
            ApproximateTokenCount = 0;
        }
    }

    private sealed class FunctionCallRecordingFilter(List<FunctionCallRecord> records) : IFunctionInvocationFilter
    {
        private readonly List<FunctionCallRecord> _records = records;

        public async Task OnFunctionInvocationAsync(Microsoft.SemanticKernel.FunctionInvocationContext context, Func<Microsoft.SemanticKernel.FunctionInvocationContext, Task> next)
        {
            string request = string.Join(", ", context.Arguments.Select(a => $"{a.Key}: {a.Value}"));
            await next(context);

            string response = context.Result?.GetValue<object>()?.ToString() ?? context.Result?.ToString() ?? string.Empty;
            string server = context.Function.PluginName ?? "McpServer";
            string function = context.Function.Name;
            _records.Add(new FunctionCallRecord(server, function, request, response));
        }
    }

    public event Action<bool>? LoadingStateChanged;
    public event Action? ChatInitialized;
    public event Func<IAppChatMessage, Task>? MessageAdded;
    public event Func<IAppChatMessage, Task>? MessageUpdated;
    public event Func<Guid, Task>? MessageDeleted;

    public bool IsLoading { get; private set; }
    public ObservableCollection<IAppChatMessage> Messages { get; } = [];
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
        _streamingManager = new StreamingMessageManager(MessageUpdated);
        _agentDescriptions = agentsList;

        AddSystemMessages();
        ChatInitialized?.Invoke();
    }

    private void AddSystemMessages()
    {
        foreach (var agent in _agentDescriptions)
        {
            var systemMessage = new AppChatMessage(agent.Content, DateTime.Now, ChatRole.System, string.Empty);
            Messages.Add(systemMessage);
        }
    }

    public void ClearChat()
    {
        Messages.Clear();
        _agentDescriptions.Clear();
        _activeStreams.Clear();
    }

    public async Task CancelAsync()
    {
        _cancellationTokenSource?.Cancel();

        if (_streamingManager != null && _activeStreams.Any())
        {
            await CancelActiveStreams();
        }

        UpdateLoadingState(false);
    }

    private async Task CancelActiveStreams()
    {
        foreach (var state in _activeStreams.Values.ToList())
        {
            AppChatMessage canceledMessage = _streamingManager.CancelStreaming(state.Message);
            await ReplaceStreamingMessageWithFinal(state.Message, canceledMessage);
        }
        _activeStreams.Clear();
    }

    public async Task AddUserMessageAndAnswerAsync(string text, ChatConfiguration chatConfiguration, IReadOnlyList<ChatMessageFile>? files = null)
    {
        if (string.IsNullOrWhiteSpace(text) || IsLoading)
            return;

        var trimmedText = text.Trim();
        await AddMessageAsync(new AppChatMessage(trimmedText, DateTime.Now, ChatRole.User, string.Empty, files));
        UpdateLoadingState(true);

        _cancellationTokenSource = new CancellationTokenSource();
        try
        {
            await ProcessAIResponseAsync(chatConfiguration, trimmedText, _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            await HandleError($"An error occurred while getting the response: {ex.Message}");
        }
        finally
        {
            Cleanup();
        }
    }

    private async Task AddMessageAsync(IAppChatMessage message)
    {
        Messages.Add(message);
        await (MessageAdded?.Invoke(message) ?? Task.CompletedTask);
    }


    private async Task ProcessAIResponseAsync(ChatConfiguration chatConfiguration, string userMessage, CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing response with model: {ModelName}", chatConfiguration.ModelName);

        var functionCalls = new List<FunctionCallRecord>();
        var debouncers = new Dictionary<string, StreamingDebouncer>();

        // Стриминговое сообщение будет создано динамически для каждого агента при первом ответе
        // Нет зависимости от количества агентов

        try
        {
            await ProcessWithRuntime(chatConfiguration, userMessage, functionCalls, debouncers, cancellationToken);
        }
        catch (ModelDoesNotSupportToolsException ex)
        {
            await HandleModelNotSupportingTools(ex, chatConfiguration);
        }
        catch (Exception ex)
        {
            await HandleGeneralError(ex);
        }
        finally
        {
            await FinalizeProcessing(functionCalls, debouncers, chatConfiguration);
        }
    }

    private async Task ProcessWithRuntime(ChatConfiguration chatConfiguration, string userMessage,
        List<FunctionCallRecord> functionCalls, Dictionary<string, StreamingDebouncer> debouncers,
        CancellationToken cancellationToken)
    {
        var runtime = new InProcessRuntime();
        await runtime.StartAsync();

        var trackingFilter = new FunctionCallRecordingFilter(functionCalls);
        var agents = await CreateAgents(chatConfiguration, userMessage, trackingFilter);
        var groupChatManager = CreateGroupChatManager(chatConfiguration);
        var chatOrchestration = CreateChatOrchestration(groupChatManager, agents, functionCalls, debouncers, chatConfiguration);

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
            RemoveTrackingFilters(agents, trackingFilter);
        }
    }

    private async Task<List<ChatCompletionAgent>> CreateAgents(
        ChatConfiguration chatConfiguration,
        string userMessage,
        FunctionCallRecordingFilter trackingFilter)
    {
        var agents = new List<ChatCompletionAgent>();

        foreach (var desc in _agentDescriptions)
        {
            var agentKernel = await kernelService.CreateKernelAsync(
                chatConfiguration with
                {
                    Functions = desc.Functions,
                    ModelName = string.IsNullOrWhiteSpace(desc.ModelName)
                        ? chatConfiguration.ModelName
                        : desc.ModelName
                },
                desc.AutoSelectCount > 0 ? userMessage : null,
                desc.AutoSelectCount > 0 ? desc.AutoSelectCount : null);

            agentKernel.FunctionInvocationFilters.Add(trackingFilter);

            agents.Add(new ChatCompletionAgent
            {
                Name = !string.IsNullOrWhiteSpace(desc.AgentName) ? desc.AgentName : desc.Name,
                Description = desc.Name,
                Instructions = desc.Content,
                Kernel = agentKernel
            });
        }

        return agents;
    }

    private RoundRobinGroupChatManager CreateGroupChatManager(ChatConfiguration chatConfiguration)
    {
        return _agentDescriptions.Count == 1
            ? new SingleAgentGroupChatManager()
            : new ReasonableRoundRobinGroupChatManager(chatConfiguration.StopAgentName, chatConfiguration.StopPhrase)
            {
                MaximumInvocationCount = chatConfiguration.MaximumInvocationCount
            };
    }

    private GroupChatOrchestration CreateChatOrchestration(
        RoundRobinGroupChatManager groupChatManager,
        List<ChatCompletionAgent> agents,
        List<FunctionCallRecord> functionCalls,
        Dictionary<string, StreamingDebouncer> debouncers,
        ChatConfiguration chatConfiguration)
    {
        return new GroupChatOrchestration(groupChatManager, agents.ToArray())
        {
            // Стандартный колбэк для финальных сообщений
            ResponseCallback = async message =>
            {
                if (message.Role != AuthorRole.Assistant)
                {
                    await HandleNonAssistantMessage(message);
                    return;
                }
                await HandleAssistantMessage(message, functionCalls, debouncers);
            },
            // Новый стриминговый колбэк для инкрементальных токенов
            StreamingResponseCallback = async (streamingContent, isFinal) =>
            {
                var agentName = streamingContent.AuthorName;
                if (string.IsNullOrWhiteSpace(agentName))
                    agentName = _agentDescriptions.FirstOrDefault()?.Name ?? "Assistant";

                if (!_activeStreams.TryGetValue(agentName, out var state))
                    state = await CreateStreamingState(agentName, functionCalls, debouncers);

                if (!string.IsNullOrEmpty(streamingContent.Content))
                    UpdateStreamingMessage(state, streamingContent.Content, agentName, debouncers);

                if (isFinal)
                    await CompleteStreamingMessage(state, functionCalls, chatConfiguration);
            }
        };
    }

    private async Task HandleNonAssistantMessage(ChatMessageContent message)
    {
        var role = message.Role switch
        {
            var r when r == AuthorRole.System => ChatRole.System,
            var r when r == AuthorRole.User => ChatRole.User,
            _ => ChatRole.Assistant
        };

        var appMessage = new AppChatMessage(message.Content ?? string.Empty, DateTime.Now, role, message.AuthorName ?? string.Empty);
        await AddMessageAsync(appMessage);
    }

    private async Task HandleAssistantMessage(
        ChatMessageContent message,
        List<FunctionCallRecord> functionCalls,
        Dictionary<string, StreamingDebouncer> debouncers)
    {
        // Ensure we always have a valid agent name, especially for single agent scenarios
        var agentName = message.AuthorName;
        if (string.IsNullOrWhiteSpace(agentName))
        {
            agentName = _agentDescriptions.FirstOrDefault()?.Name ?? "Assistant";
        }

        if (!_activeStreams.TryGetValue(agentName, out var state))
        {
            state = await CreateStreamingState(agentName, functionCalls, debouncers);
        }

        if (!string.IsNullOrEmpty(message.Content))
        {
            UpdateStreamingMessage(state, message.Content, agentName, debouncers);
        }
    }

    private async Task<StreamState> CreateStreamingState(
        string agentName,
        List<FunctionCallRecord> functionCalls,
        Dictionary<string, StreamingDebouncer> debouncers)
    {
        var stream = _streamingManager.CreateStreamingMessage(null, agentName);
        var state = new StreamState(stream, functionCalls.Count);
        _activeStreams[agentName] = state;

        debouncers[agentName] = new StreamingDebouncer(UpdateIntervalMs, async () =>
        {
            if (MessageUpdated != null)
                await MessageUpdated(state.Message);
        });

        await AddMessageAsync(stream);
        return state;
    }

    private void UpdateStreamingMessage(
        StreamState state,
        string content,
        string agentName,
        Dictionary<string, StreamingDebouncer> debouncers)
    {
        state.Message.Append(content);
        state.ApproximateTokenCount++;

        if (debouncers.TryGetValue(agentName, out var debouncer))
        {
            debouncer.TriggerUpdate();
        }
    }

    private static void RemoveTrackingFilters(List<ChatCompletionAgent> agents, FunctionCallRecordingFilter trackingFilter)
    {
        foreach (var agent in agents)
        {
            agent.Kernel.FunctionInvocationFilters.Remove(trackingFilter);
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

    private async Task HandleGeneralError(Exception ex)
    {
        const string errorPrefix = "Agent Error";
        logger.LogError(ex, "Error processing response");
        ClearActiveStreams();
        await HandleError($"{errorPrefix}: {ex.Message}");
    }

    private void ClearActiveStreams()
    {
        foreach (var state in _activeStreams.Values.ToList())
        {
            RemoveStreamingMessage(state.Message);
        }
        _activeStreams.Clear();
    }

    private async Task FinalizeProcessing(
        List<FunctionCallRecord> functionCalls,
        Dictionary<string, StreamingDebouncer> debouncers,
        ChatConfiguration chatConfiguration)
    {
        await FlushDebouncers(debouncers);
        await CompleteActiveStreams(functionCalls, chatConfiguration);
    }

    private static async Task FlushDebouncers(Dictionary<string, StreamingDebouncer> debouncers)
    {
        foreach (var debouncer in debouncers.Values)
        {
            await debouncer.FlushAsync();
            debouncer.Dispose();
        }
    }

    private async Task CompleteActiveStreams(List<FunctionCallRecord> functionCalls, ChatConfiguration chatConfiguration)
    {
        foreach (var kvp in _activeStreams.ToList())
        {
            await CompleteStreamingMessage(kvp.Value, functionCalls, chatConfiguration);
            _activeStreams.Remove(kvp.Key);
        }
    }

    private async Task CompleteStreamingMessage(
        StreamState state,
        List<FunctionCallRecord> functionCalls,
        ChatConfiguration chatConfiguration)
    {
        await (MessageUpdated?.Invoke(state.Message) ?? Task.CompletedTask);

        var messageFunctionCalls = functionCalls.Skip(state.FunctionCallStartIndex).ToList();
        foreach (var fc in messageFunctionCalls)
        {
            state.Message.AddFunctionCall(fc);
        }

        TimeSpan processingTime = DateTime.Now - state.StartTime;

        var statistics = _streamingManager.BuildStatistics(
            processingTime,
            chatConfiguration,
            state.ApproximateTokenCount,
            messageFunctionCalls.Select(fc => fc.Server).Distinct());

        var finalMessage = _streamingManager.CompleteStreaming(state.Message, statistics);
        await ReplaceStreamingMessageWithFinal(state.Message, finalMessage);
    }



    private async Task ReplaceStreamingMessageWithFinal(StreamingAppChatMessage streamingMessage, AppChatMessage finalMessage)
    {
        int index = Messages.IndexOf(streamingMessage);

        if (index >= 0)
        {
            Messages[index] = finalMessage;
            await (MessageUpdated?.Invoke(finalMessage) ?? Task.CompletedTask);
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
    private void Cleanup()
    {
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        UpdateLoadingState(false);
    }

    private void UpdateLoadingState(bool isLoading)
    {
        IsLoading = isLoading;
        LoadingStateChanged?.Invoke(isLoading);
    }

    public async Task DeleteMessageAsync(Guid id)
    {
        if (IsLoading)
            return;

        var message = Messages.FirstOrDefault(m => m.Id == id);
        if (message == null)
            return;

        Messages.Remove(message);
        await (MessageDeleted?.Invoke(id) ?? Task.CompletedTask);
    }
}

#pragma warning restore SKEXP0110
