using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.Agents.Orchestration.Transforms;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;
using OllamaSharp.Models.Exceptions;
using System.Collections.ObjectModel;

namespace ChatClient.Api.Client.Services;

public class AppChatService(
    KernelService kernelService,
    ILogger<AppChatService> logger,
    IAppChatHistoryBuilder chatHistoryBuilder,
    AppForceLastUserReducer reducer,
    IOllamaKernelService ollamaKernelService,
    IOpenAIClientService openAIClientService,
    IUserSettingsService userSettingsService) : IAppChatService
{
    private CancellationTokenSource? _cancellationTokenSource;
    private AppStreamingMessageManager _streamingManager = null!;
    private readonly Dictionary<string, StreamingAppChatMessage> _activeStreams = new();
    private const string PlaceholderAgent = "__placeholder__";
    private Dictionary<string, AgentDescription> _agentsByName = new();

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

    public IReadOnlyCollection<AgentDescription> AgentDescriptions => _agentsByName.Values;

    public void InitializeChat(IReadOnlyCollection<AgentDescription> agents)
    {
        logger.LogInformation("Initializing chat with {AgentCount} agents", agents?.Count);
        if (agents is null)
            throw new ArgumentNullException(nameof(agents));

        if (agents.Count == 0)
            throw new ArgumentException("At least one agent must be selected.", nameof(agents));

        Messages.Clear();
        _activeStreams.Clear();
        _streamingManager = new AppStreamingMessageManager();

        _agentsByName = agents.ToDictionary(
            desc => desc.AgentId,
            desc => desc,
            StringComparer.OrdinalIgnoreCase);

        ChatReset?.Invoke();
    }

    public void ResetChat()
    {
        logger.LogInformation("Resetting chat");
        Messages.Clear();
        _activeStreams.Clear();
        ChatReset?.Invoke();
    }

    public async Task CancelAsync()
    {
        logger.LogInformation("Cancellation requested");
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
            if (message.AgentName == "?")
            {
                await (MessageDeleted?.Invoke(message.Id) ?? Task.CompletedTask);
                continue;
            }

            var canceled = _streamingManager.CancelStreaming(message);
            Messages.Add(canceled);
            await (MessageUpdated?.Invoke(canceled, true) ?? Task.CompletedTask);
        }
        _activeStreams.Clear();
    }

    /// <summary>
    /// Modern implementation using ChatCompletionAgent without obsolete methods
    /// </summary>
    public async Task GenerateAnswerAsync(string text, AppChatConfiguration chatConfiguration, GroupChatManager groupChatManager, IReadOnlyList<AppChatMessageFile>? files = null)
    {
        logger.LogInformation("GenerateAnswerAsync called with text length {Length}", text?.Length);
        if (string.IsNullOrWhiteSpace(text) || IsAnswering)
            return;

        var userMessage = new AppChatMessage(text, DateTime.Now, ChatRole.User, string.Empty, files);
        await AddMessageAsync(userMessage);

        var placeholder = _activeStreams[PlaceholderAgent] = CreateInitialPlaceholderMessage();
        await NotifyMessageAddedAsync(placeholder);

        using var trackingScope = CreateTrackingScope();
        UpdateAnsweringState(true);
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            logger.LogInformation("Processing response with modern agent architecture");

            var runtime = new InProcessRuntime();
            await runtime.StartAsync(_cancellationTokenSource.Token);

            var agents = await CreateModernAgentsAsync(text, trackingScope, chatConfiguration, _cancellationTokenSource.Token);

            OrchestrationInputTransform<string> inputTransform = async (_, ct) =>
            {
                var filteredMessages = Messages.Where(msg =>
                {
                    if (msg.IsStreaming)
                    {
                        return false; // Skip streaming placeholders
                    }
                    return !string.IsNullOrWhiteSpace(msg.Content) || (msg.Files?.Count > 0);
                }).ToList();

                logger.LogInformation("Building chat history from {TotalMessages} messages, filtered to {FilteredCount}",
                    Messages.Count, filteredMessages.Count);

                var firstAgent = agents.FirstOrDefault();
                if (firstAgent?.Kernel == null)
                    throw new InvalidOperationException("No agents available or agent kernel is null");

                var agentId = _agentsByName[firstAgent.Name!].Id;
                var built = await chatHistoryBuilder.BuildChatHistoryAsync(filteredMessages, firstAgent.Kernel, agentId, ct);

                var rag = built.FirstOrDefault(m => m.Role == AuthorRole.Tool);
                var ragText = rag?.Items.OfType<Microsoft.SemanticKernel.TextContent>().FirstOrDefault()?.Text;
                if (!string.IsNullOrWhiteSpace(ragText) && !Messages.Any(m => m.Role == ChatRole.Tool))
                {
                    await AddMessageAsync(new AppChatMessage(ragText, DateTime.Now, ChatRole.Tool));
                }

                return built;
            };

            var chatOrchestration = CreateChatOrchestration(
                groupChatManager,
                agents,
                trackingScope.Filters.Values.Sum(f => f.Records.Count),
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
            await FinalizeProcessing(trackingScope.Filters.Values.Sum(f => f.Records.Count), trackingScope);
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

    private Task NotifyMessageAddedAsync(IAppChatMessage message) =>
        MessageAdded?.Invoke(message) ?? Task.CompletedTask;

    /// <summary>
    /// Modern implementation of agent creation using ChatCompletionAgent constructor without obsolete methods
    /// </summary>
    private async Task<List<ChatCompletionAgent>> CreateModernAgentsAsync(
        string userMessage,
        TrackingFiltersScope trackingScope,
        AppChatConfiguration chatConfiguration,
        CancellationToken cancellationToken)
    {
        var agentNames = string.Join(", ", _agentsByName.Keys);
        logger.LogInformation("Creating {AgentCount} modern agents: [{AgentNames}]", _agentsByName.Count, agentNames);
        var agents = new List<ChatCompletionAgent>();

        foreach (var desc in _agentsByName.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var functionsToRegister = await kernelService.GetFunctionsToRegisterAsync(desc.FunctionSettings, userMessage, cancellationToken);
            var modelName = desc.ModelName ?? chatConfiguration.ModelName ?? throw new InvalidOperationException($"Agent '{desc.AgentName}' model name is not set and no default model is configured.");

            var agentName = desc.AgentId;

            var trackingFilter = new FunctionCallRecordingFilter();
            trackingScope.Register(agentName, trackingFilter, () => { /* Cleanup will be handled by scope */ });

            logger.LogDebug("Configuring modern agent {AgentName} with model {ModelName}", agentName, modelName);
            var settings = new PromptExecutionSettings
            {
                ModelId = modelName,
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

            var kernel = await CreateModernKernelAsync(new ServerModel(desc.LlmId ?? Guid.Empty, modelName), functionsToRegister, desc.AgentName, cancellationToken);

            kernel.FunctionInvocationFilters.Add(trackingFilter);

            var agent = new ChatCompletionAgent
            {
                Name = agentName,
                Description = desc.AgentName,
                Instructions = desc.Content,
                Kernel = kernel,
                Arguments = new KernelArguments(settings),
                HistoryReducer = reducer
            };

            agents.Add(agent);
        }

        return agents;
    }

    private async Task<ServerType> GetServerTypeAsync(Guid serverId)
    {
        var server = await LlmServerConfigHelper.GetServerConfigAsync(userSettingsService, serverId);
        
        if (server == null)
            throw new InvalidOperationException($"Server configuration not found for ID: {serverId}");
            
        return server.ServerType;
    }

    /// <summary>
    /// Modern kernel creation method that creates a basic kernel for ChatCompletionAgent
    /// This is a simplified approach since ChatCompletionAgent handles most of the complexity
    /// </summary>
    private async Task<Kernel> CreateModernKernelAsync(
        ServerModel serverModel,
        IEnumerable<string>? functionsToRegister,
        string agentName,
        CancellationToken cancellationToken = default)
    {
        var builder = Kernel.CreateBuilder();

        builder.Services.AddLogging(c => c.AddConsole().SetMinimumLevel(LogLevel.Information));

        var serverType = await GetServerTypeAsync(serverModel.ServerId);
        IChatCompletionService baseChatService;

        if (serverType == ServerType.ChatGpt)
        {
            baseChatService = await openAIClientService.GetClientAsync(serverModel, cancellationToken);
        }
        else
        {
            baseChatService = await ollamaKernelService.GetClientAsync(serverModel.ServerId);
        }

        builder.Services.AddSingleton<IChatCompletionService>(_ =>
            new AppForceLastUserChatCompletionService(baseChatService, reducer));

        var kernel = builder.Build();

        if (functionsToRegister != null && functionsToRegister.Any())
        {
            await kernelService.RegisterMcpToolsPublicAsync(kernel, functionsToRegister, cancellationToken);
            logger.LogInformation("MCP tools registered for modern agent {AgentName}: [{Functions}]", agentName, string.Join(", ", functionsToRegister));
        }

        return kernel;
    }

    private GroupChatOrchestration CreateChatOrchestration(
        GroupChatManager groupChatManager,
        List<ChatCompletionAgent> agents,
        int functionCount,
        TrackingFiltersScope trackingScope,
        OrchestrationInputTransform<string> inputTransform)
    {
        return new GroupChatOrchestration(groupChatManager, agents.ToArray())
        {
            InputTransform = inputTransform,
            // Streaming callback for incremental tokens
            StreamingResponseCallback = async (streamingContent, isFinal) =>
            {
                var agentName = streamingContent.AuthorName;
                if (string.IsNullOrWhiteSpace(agentName))
                    agentName = _agentsByName.Values.FirstOrDefault()?.AgentName ?? "Assistant";

                if (!_activeStreams.TryGetValue(agentName, out var message))
                {
                    if (_activeStreams.TryGetValue(PlaceholderAgent, out var placeholder))
                    {
                        logger.LogDebug("Replacing placeholder with agent {AgentName}, placeholder content length: {ContentLength}",
                                       agentName, placeholder.Content.Length);

                        _activeStreams.Remove(PlaceholderAgent);
                        placeholder.SetAgentName(agentName);
                        placeholder.ResetContent();
                        _activeStreams[agentName] = placeholder;
                        message = placeholder;

                        logger.LogDebug("Placeholder replaced with agent {AgentName}, new content length: {ContentLength}",
                                       agentName, placeholder.Content.Length);

                        // Force an update after agent name change to ensure UI sees the change
                        if (MessageUpdated != null)
                        {
                            logger.LogDebug("Sending forced update for agent name change: {AgentName}", agentName);
                            await MessageUpdated(message, false);
                        }
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
                    var final = CompleteStreamingMessage(message, functionCount, trackingScope);
                    Messages.Add(final);
                    await (MessageUpdated?.Invoke(final, true) ?? Task.CompletedTask);
                    _activeStreams.Remove(agentName);

                    if (_agentsByName.Count > 1)
                    {
                        var next = CreateNextAgentPlaceholder();
                        _activeStreams[PlaceholderAgent] = next;
                        await NotifyMessageAddedAsync(next);
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

        await NotifyMessageAddedAsync(stream);
        return stream;
    }

    private async Task UpdateStreamingMessage(
        StreamingAppChatMessage message,
        string content)
    {
        logger.LogDebug("UpdateStreamingMessage called for agent {AgentName} with content: {Content}",
                       message.AgentName, content);

        message.Append(content);
        message.ApproximateTokenCount++;

        if (MessageUpdated != null)
        {
            logger.LogDebug("Invoking MessageUpdated event for agent {AgentName}, content length: {ContentLength}",
                           message.AgentName, message.Content.Length);
            await MessageUpdated(message, false);
            logger.LogDebug("MessageUpdated event completed for agent {AgentName}", message.AgentName);
        }
        else
        {
            logger.LogWarning("MessageUpdated event is null - streaming updates not being propagated for agent {AgentName}",
                             message.AgentName);
        }
    }

    private async Task HandleModelNotSupportingTools(ModelDoesNotSupportToolsException ex, AppChatConfiguration chatConfiguration)
    {
        logger.LogWarning(ex, "Model {ModelName} does not support function calling", chatConfiguration.ModelName);

        await ClearActiveStreams();

        string errorMessage = chatConfiguration.Functions.Any()
            ? $"⚠️ The model **{chatConfiguration.ModelName}** does not support function calling. Please either:\n\n" +
              "• Switch to a model that supports function calling\n" +
              "• Disable all functions for this conversation\n\n" +
              "You can see which models support function calling on the Models page."
            : $"⚠️ The model **{chatConfiguration.ModelName}** does not support the requested functionality.";

        await HandleError(errorMessage);
    }

    private async Task ClearActiveStreams()
    {
        foreach (var message in _activeStreams.Values.ToList())
        {
            await (MessageDeleted?.Invoke(message.Id) ?? Task.CompletedTask);
        }
        _activeStreams.Clear();
    }

    private async Task FinalizeProcessing(
        int functionCount,
        TrackingFiltersScope trackingScope)
    {
        await RemoveDanglingPlaceholderAsync();
        await CompleteActiveStreams(functionCount, trackingScope);
    }

    private async Task CompleteActiveStreams(int functionCount, TrackingFiltersScope trackingScope)
    {
        foreach (var kvp in _activeStreams.ToList())
        {
            if (kvp.Value.AgentName == "?")
            {
                await (MessageDeleted?.Invoke(kvp.Value.Id) ?? Task.CompletedTask);
            }
            else
            {
                var final = CompleteStreamingMessage(kvp.Value, functionCount, trackingScope);
                Messages.Add(final);
                await (MessageUpdated?.Invoke(final, true) ?? Task.CompletedTask);
            }
            _activeStreams.Remove(kvp.Key);
        }
    }

    private AppChatMessage CompleteStreamingMessage(
        StreamingAppChatMessage message,
        int functionCount,
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

        var processingTime = DateTime.Now - message.MsgDateTime;

        var modelName = !string.IsNullOrEmpty(message.AgentName) && _agentsByName.TryGetValue(message.AgentName, out var agentDesc)
            ? agentDesc.ModelName ?? string.Empty
            : string.Empty;

        var statistics = _streamingManager.BuildStatistics(
            processingTime,
            modelName,
            message.ApproximateTokenCount,
            functionCount,
            messageFunctionCalls.Select(fc => fc.Server).Distinct());

        return _streamingManager.CompleteStreaming(message, statistics);
    }

    private async Task RemoveDanglingPlaceholderAsync()
    {
        if (_activeStreams.TryGetValue(PlaceholderAgent, out var placeholder) && placeholder.AgentName == "?")
        {
            _activeStreams.Remove(PlaceholderAgent);
            await (MessageDeleted?.Invoke(placeholder.Id) ?? Task.CompletedTask);
        }
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
        logger.LogInformation("Deleting message {MessageId}", id);
        if (IsAnswering)
            return;

        var message = Messages.FirstOrDefault(m => m.Id == id);
        if (message == null)
            return;

        Messages.Remove(message);
        await (MessageDeleted?.Invoke(id) ?? Task.CompletedTask);
    }
}
