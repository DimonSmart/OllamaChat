using System.Collections.ObjectModel;

using ChatClient.Api.Services;
using ChatClient.Shared.Models;

using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.Agents.Orchestration.Transforms;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;

using OllamaSharp.Models.Exceptions;
#pragma warning disable SKEXP0110

namespace ChatClient.Api.Client.Services;

public class ChatService(
    KernelService kernelService,
    ILogger<ChatService> logger,
    IChatHistoryBuilder chatHistoryBuilder) : IChatService
{
    private CancellationTokenSource? _cancellationTokenSource;
    private StreamingMessageManager _streamingManager = null!;
    private readonly Dictionary<string, StreamingAppChatMessage> _activeStreams = new();
    private const string PlaceholderAgent = "__placeholder__";
    private IReadOnlyCollection<AgentDescription> _agentDescriptions = [];

    public event Action<bool>? AnsweringStateChanged;
    public event Action? ChatReset;
    public event Func<IAppChatMessage, Task>? MessageAdded;
    public event Func<IAppChatMessage, bool, Task>? MessageUpdated;
    public event Func<Guid, Task>? MessageDeleted;

    public bool IsAnswering { get; private set; }
    public ObservableCollection<IAppChatMessage> Messages { get; } = [];

    private TrackingFiltersScope CreateTrackingScope()
    {
        return new TrackingFiltersScope();
    }

    public IReadOnlyCollection<AgentDescription> AgentDescriptions => _agentDescriptions;

    public void InitializeChat(IReadOnlyCollection<AgentDescription> agents)
    {
        if (agents is null)
            throw new ArgumentNullException(nameof(agents));

        if (agents.Count == 0)
            throw new ArgumentException("At least one agent must be selected.", nameof(agents));

        Messages.Clear();
        _activeStreams.Clear();
        _streamingManager = new StreamingMessageManager();
        _agentDescriptions = agents;

        ChatReset?.Invoke();
    }

    public void ResetChat()
    {
        Messages.Clear();
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

    public async Task GenerateAnswerAsync(string text, ChatConfiguration chatConfiguration, GroupChatManager groupChatManager, IReadOnlyList<ChatMessageFile>? files = null)
    {
        if (string.IsNullOrWhiteSpace(text) || IsAnswering)
            return;
        var userMessage = new AppChatMessage(text, DateTime.Now, ChatRole.User, string.Empty, files);
        await AddMessageAsync(userMessage);

        await AddMessageAsync(_activeStreams[PlaceholderAgent] = CreateInitialPlaceholderMessage());

        using var trackingScope = CreateTrackingScope();

        UpdateAnsweringState(true);

        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            logger.LogInformation("Processing response with configuration: {chatConfiguration}", chatConfiguration);

            var runtime = new InProcessRuntime();
            await runtime.StartAsync(_cancellationTokenSource.Token);

            var agents = await CreateAgents(chatConfiguration, text, trackingScope);

            OrchestrationInputTransform<string> inputTransform = async (_, ct) =>
                await chatHistoryBuilder.BuildChatHistoryAsync(Messages, agents[0].Kernel, ct);

            var chatOrchestration = CreateChatOrchestration(
                groupChatManager,
                agents,
                chatConfiguration,
                trackingScope,
                inputTransform);

            var invokeResult = await chatOrchestration.InvokeAsync(string.Empty, runtime, _cancellationTokenSource.Token);
            var _ = await invokeResult.GetValueAsync(cancellationToken: _cancellationTokenSource.Token);
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
            await FinalizeProcessing(chatConfiguration, trackingScope);
        }
    }

    private StreamingAppChatMessage CreateInitialPlaceholderMessage()
    {
        // Display a temporary placeholder while waiting for the first agent token
        var placeholder = _streamingManager.CreateStreamingMessage(agentName: "?");
        placeholder.Append("...");
        return placeholder;
    }

    private StreamingAppChatMessage CreateNextAgentPlaceholder()
    {
        var placeholder = _streamingManager.CreateStreamingMessage(agentName: "?");
        placeholder.Append("...");
        return placeholder;
    }

    private async Task AddMessageAsync(IAppChatMessage message)
    {
        Messages.Add(message);
        await (MessageAdded?.Invoke(message) ?? Task.CompletedTask);
    }

    private async Task<List<ChatCompletionAgent>> CreateAgents(ChatConfiguration chatConfiguration, string userMessage, TrackingFiltersScope trackingScope)
    {
        var agents = new List<ChatCompletionAgent>();

        foreach (var desc in _agentDescriptions)
        {
            var functionsToRegister = await kernelService.GetFunctionsToRegisterAsync(desc.FunctionSettings, userMessage);
            var modelName = string.IsNullOrWhiteSpace(desc.ModelName) ? chatConfiguration.ModelName : desc.ModelName;
            var agentKernel = await kernelService.CreateKernelAsync(modelName, functionsToRegister);

            // TODO Add function into AgentDescription to get Agent name for UI
            var agentName = !string.IsNullOrWhiteSpace(desc.ShortName) ? desc.ShortName : desc.AgentName;

            var trackingFilter = new FunctionCallRecordingFilter();
            agentKernel.FunctionInvocationFilters.Add(trackingFilter);
            trackingScope.Register(agentName, trackingFilter, () => agentKernel.FunctionInvocationFilters.Remove(trackingFilter));

            var settings = new PromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: true)
            };

            if (desc.Temperature.HasValue)
            {
                settings.ExtensionData ??= new Dictionary<string, object>();
                settings.ExtensionData["temperature"] = desc.Temperature.Value;
            }

            if (desc.RepeatPenalty.HasValue)
            {
                settings.ExtensionData ??= new Dictionary<string, object>();
                settings.ExtensionData["repeat_penalty"] = desc.RepeatPenalty.Value;
            }

            agents.Add(new ChatCompletionAgent
            {
                Name = agentName,
                Description = desc.AgentName,
                Instructions = desc.Content,
                Kernel = agentKernel,
                Arguments = new KernelArguments(settings)
            });
        }

        return agents;
    }

    private GroupChatOrchestration CreateChatOrchestration(
        GroupChatManager groupChatManager,
        List<ChatCompletionAgent> agents,
        ChatConfiguration chatConfiguration,
        TrackingFiltersScope trackingScope,
        OrchestrationInputTransform<string> inputTransform)
    {
        return new GroupChatOrchestration(groupChatManager, agents.ToArray())
        {
            InputTransform = inputTransform,
            // Стриминговый колбэк для инкрементальных токенов
            StreamingResponseCallback = async (streamingContent, isFinal) =>
            {
                var agentName = streamingContent.AuthorName;
                if (string.IsNullOrWhiteSpace(agentName))
                    agentName = _agentDescriptions.FirstOrDefault()?.AgentName ?? "Assistant";

                if (!_activeStreams.TryGetValue(agentName, out var message))
                {
                    logger.LogInformation("Agent {AgentName} started responding", agentName);
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
                    await CompleteStreamingMessage(message, chatConfiguration, trackingScope);
                    _activeStreams.Remove(agentName);

                    if (_agentDescriptions.Count > 1)
                    {
                        var placeholder = CreateNextAgentPlaceholder();
                        _activeStreams[PlaceholderAgent] = placeholder;
                        await AddMessageAsync(placeholder);
                    }
                }
            }
        };
    }

    private async Task<StreamingAppChatMessage> CreateStreamingState(
        string agentName)
    {
        var stream = _streamingManager.CreateStreamingMessage(null, agentName);
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
        ChatConfiguration chatConfiguration,
        TrackingFiltersScope trackingScope)
    {
        RemoveDanglingPlaceholder();
        await CompleteActiveStreams(chatConfiguration, trackingScope);
    }

    private async Task CompleteActiveStreams(ChatConfiguration chatConfiguration, TrackingFiltersScope trackingScope)
    {
        foreach (var kvp in _activeStreams.ToList())
        {
            await CompleteStreamingMessage(kvp.Value, chatConfiguration, trackingScope);
            _activeStreams.Remove(kvp.Key);
        }
    }

    private async Task CompleteStreamingMessage(
        StreamingAppChatMessage message,
        ChatConfiguration chatConfiguration,
        TrackingFiltersScope trackingScope)
    {
        var messageFunctionCalls = new List<FunctionCallRecord>();
        if (!string.IsNullOrEmpty(message.AgentName) &&
            trackingScope.Filters.TryGetValue(message.AgentName, out var filter))
        {
            messageFunctionCalls = filter.Records.ToList();
            foreach (var fc in messageFunctionCalls)
            {
                message.AddFunctionCall(fc);
            }
            filter.Clear();
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

    private void RemoveDanglingPlaceholder()
    {
        if (_activeStreams.TryGetValue(PlaceholderAgent, out var placeholder) && placeholder.AgentName == "?")
        {
            RemoveStreamingMessage(placeholder);
            _activeStreams.Remove(PlaceholderAgent);
        }
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
