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
    private List<AgentDescription> _agentDescriptions = [];

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

    public event Action<bool>? AnsweringStateChanged;
    public event Action? ChatReset;
    public event Func<IAppChatMessage, Task>? MessageAdded;
    public event Func<IAppChatMessage, bool, Task>? MessageUpdated;
    public event Func<Guid, Task>? MessageDeleted;

    public bool IsAnswering { get; private set; }
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

        var trimmedText = text.Trim();
        await AddMessageAsync(new AppChatMessage(trimmedText, DateTime.Now, ChatRole.User, string.Empty, files));
        UpdateAnsweringState(true);

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

        // Стриминговое сообщение будет создано динамически для каждого агента при первом ответе
        // Нет зависимости от количества агентов

        try
        {
            await ProcessWithRuntime(chatConfiguration, userMessage, functionCalls, cancellationToken);
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
            await FinalizeProcessing(functionCalls, chatConfiguration);
        }
    }

    private async Task ProcessWithRuntime(ChatConfiguration chatConfiguration, string userMessage,
        List<FunctionCallRecord> functionCalls,
        CancellationToken cancellationToken)
    {
        var runtime = new InProcessRuntime();
        await runtime.StartAsync();

        var trackingFilter = new FunctionCallRecordingFilter(functionCalls);
        var agents = await CreateAgents(chatConfiguration, userMessage, trackingFilter);
        var groupChatManager = CreateGroupChatManager(chatConfiguration);
        var chatOrchestration = CreateChatOrchestration(groupChatManager, agents, functionCalls, chatConfiguration);

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
            ? new RoundRobinGroupChatManager() { MaximumInvocationCount = 1 }
            : new ReasonableRoundRobinGroupChatManager(chatConfiguration.StopAgentName, chatConfiguration.StopPhrase)
            {
                MaximumInvocationCount = chatConfiguration.MaximumInvocationCount
            };
    }

    private GroupChatOrchestration CreateChatOrchestration(
        RoundRobinGroupChatManager groupChatManager,
        List<ChatCompletionAgent> agents,
        List<FunctionCallRecord> functionCalls,
        ChatConfiguration chatConfiguration)
    {
        return new GroupChatOrchestration(groupChatManager, agents.ToArray())
        {
            // Стриминговый колбэк для инкрементальных токенов
            StreamingResponseCallback = async (streamingContent, isFinal) =>
            {
                var agentName = streamingContent.AuthorName;
                if (string.IsNullOrWhiteSpace(agentName))
                    agentName = _agentDescriptions.FirstOrDefault()?.Name ?? "Assistant";

                if (!_activeStreams.TryGetValue(agentName, out var message))
                    message = await CreateStreamingState(agentName, functionCalls);

                if (!string.IsNullOrEmpty(streamingContent.Content))
                    await UpdateStreamingMessage(message, streamingContent.Content);

                if (isFinal)
                {
                    await CompleteStreamingMessage(message, functionCalls, chatConfiguration);
                    _activeStreams.Remove(agentName);
                }
            }
        };
    }

    private async Task<StreamingAppChatMessage> CreateStreamingState(
        string agentName,
        List<FunctionCallRecord> functionCalls)
    {
        var stream = _streamingManager.CreateStreamingMessage(null, agentName);
        stream.FunctionCallStartIndex = functionCalls.Count;
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
        foreach (var message in _activeStreams.Values.ToList())
        {
            RemoveStreamingMessage(message);
        }
        _activeStreams.Clear();
    }

    private async Task FinalizeProcessing(
        List<FunctionCallRecord> functionCalls,
        ChatConfiguration chatConfiguration)
    {
        await CompleteActiveStreams(functionCalls, chatConfiguration);
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
        StreamingAppChatMessage message,
        List<FunctionCallRecord> functionCalls,
        ChatConfiguration chatConfiguration)
    {
        var messageFunctionCalls = functionCalls.Skip(message.FunctionCallStartIndex).ToList();
        foreach (var fc in messageFunctionCalls)
        {
            message.AddFunctionCall(fc);
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
    private void Cleanup()
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
