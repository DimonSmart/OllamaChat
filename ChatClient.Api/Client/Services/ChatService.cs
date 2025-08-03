using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using OllamaSharp.Models.Exceptions;
#pragma warning disable SKEXP0110

namespace ChatClient.Api.Client.Services;

public class ChatService(
    KernelService kernelService,
    IChatHistoryBuilder historyBuilder,
    ILogger<ChatService> logger) : IChatService
{
    private CancellationTokenSource? _cancellationTokenSource;
    private StreamingMessageManager _streamingManager = null!;
    private StreamingAppChatMessage? _currentStreamingMessage;
    private List<SystemPrompt> _agentDescriptions = [];
    private AgentGroupChat _groupChat = new();
    private ChatCompletionAgentCoordinator? _agentCoordinator;
    public event Action<bool>? LoadingStateChanged;
    public event Action? ChatInitialized;
    public event Func<IAppChatMessage, Task>? MessageAdded;
    public event Func<IAppChatMessage, Task>? MessageUpdated;
    public event Func<Guid, Task>? MessageDeleted;

    public bool IsLoading { get; private set; }
    public ObservableCollection<IAppChatMessage> Messages { get; } = [];
    public IReadOnlyList<SystemPrompt> AgentDescriptions => _agentDescriptions;

    public void InitializeChat(IEnumerable<SystemPrompt> initialAgents)
    {
        if (initialAgents is null)
        {
            throw new ArgumentNullException(nameof(initialAgents));
        }

        var agentsList = initialAgents.ToList();
        if (agentsList.Count == 0)
        {
            throw new ArgumentException("At least one agent must be selected.", nameof(initialAgents));
        }

        Messages.Clear();
        _streamingManager = new StreamingMessageManager(MessageUpdated);
        _agentDescriptions = agentsList;
        _groupChat = new AgentGroupChat();
        _agentCoordinator = new ChatCompletionAgentCoordinator(_agentDescriptions);

        foreach (var agent in _agentDescriptions)
        {
            var systemMessage = new AppChatMessage(agent.Content, DateTime.Now, ChatRole.System, string.Empty);
            Messages.Add(systemMessage);
        }

        ChatInitialized?.Invoke();
    }

    public void ClearChat()
    {
        Messages.Clear();
        _agentDescriptions.Clear();
        _groupChat = new AgentGroupChat();
        _agentCoordinator = null;
    }

    public async Task CancelAsync()
    {
        _cancellationTokenSource?.Cancel();

        // Handle the current streaming message if it exists
        if (_streamingManager != null && _currentStreamingMessage != null)
        {
            AppChatMessage canceledMessage = _streamingManager.CancelStreaming(_currentStreamingMessage);
            await ReplaceStreamingMessageWithFinal(_currentStreamingMessage, canceledMessage);
            _currentStreamingMessage = null;
        }

        UpdateLoadingState(false);
    }

    public async Task AddUserMessageAndAnswerAsync(string text, ChatConfiguration chatConfiguration, IReadOnlyList<ChatMessageFile>? files = null)
    {
        if (string.IsNullOrWhiteSpace(text) || IsLoading)
        {
            return;
        }

        var trimmedText = text.Trim();
        await AddMessageAsync(new AppChatMessage(trimmedText, DateTime.Now, ChatRole.User, string.Empty, files));
        _groupChat?.AddChatMessage(new ChatMessageContent(AuthorRole.User, trimmedText));
        UpdateLoadingState(true);

        _cancellationTokenSource = new CancellationTokenSource();
        try
        {
            string currentInput = trimmedText;
            int cycleCount = 0;
            while (true)
            {
                await ProcessAIResponseAsync(chatConfiguration, currentInput, _cancellationTokenSource.Token);
                cycleCount++;
                if (!chatConfiguration.AutoContinue)
                    break;
                if (!chatConfiguration.UseAgentResponses)
                    break;
                if (_agentCoordinator is null || !_agentCoordinator.ShouldContinueConversation(cycleCount))
                    break;
                if (_cancellationTokenSource.IsCancellationRequested)
                    break;
                if (Messages.LastOrDefault() is not AppChatMessage lastMessage)
                    break;
                currentInput = lastMessage.Content;
            }
        }
        catch (OperationCanceledException)
        {
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
        string responseType = chatConfiguration.UseAgentResponses ? "Agent" : "Ask";
        logger.LogInformation("Processing {ResponseType} response with model: {ModelName}", responseType, chatConfiguration.ModelName);

        List<FunctionCallRecord> functionCalls = [];
        var agentPrompt = chatConfiguration.UseAgentResponses ? _agentCoordinator?.GetNextAgentPrompt() : null;
        string? agentName = chatConfiguration.UseAgentResponses
            ? (!string.IsNullOrWhiteSpace(agentPrompt?.AgentName) ? agentPrompt?.AgentName : agentPrompt?.Name)
            : null;
        StreamingAppChatMessage streamingMessage = _streamingManager.CreateStreamingMessage(functionCalls, agentName);
        _currentStreamingMessage = streamingMessage;
        await AddMessageAsync(streamingMessage);

        var lastUpdateTime = DateTime.MinValue;
        const int updateIntervalMs = 500;
        var approximateTokenCount = 0;
        var startTime = DateTime.Now;

        try
        {
            var kernel = await kernelService.CreateKernelAsync(
                chatConfiguration,
                chatConfiguration.AutoSelectFunctions ? userMessage : null,
                chatConfiguration.AutoSelectFunctions ? chatConfiguration.AutoSelectCount : null);

            var promptExecutionSettings = new PromptExecutionSettings
            {
                FunctionChoiceBehavior = chatConfiguration.AutoSelectFunctions || chatConfiguration.Functions.Count != 0
                        ? FunctionChoiceBehavior.Auto()
                        : FunctionChoiceBehavior.None(),
            };

            var trackingFilter = new FunctionCallRecordingFilter(functionCalls);

            try
            {
                kernel.FunctionInvocationFilters.Add(trackingFilter);

                if (chatConfiguration.UseAgentResponses && agentPrompt != null)
                {
                    var agent = new ChatCompletionAgent
                    {
                        Name = agentName ?? string.Empty,
                        Instructions = agentPrompt.Content,
                        Kernel = kernel,
                    };

                    await foreach (var content in _groupChat.InvokeAsync(agent, cancellationToken))
                    {
                        await Task.Yield();
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!string.IsNullOrEmpty(content.Content))
                        {
                            streamingMessage.Append(content.Content);
                            approximateTokenCount++;
                            DateTime now = DateTime.Now;
                            if ((now - lastUpdateTime).TotalMilliseconds >= updateIntervalMs)
                            {
                                await (MessageUpdated?.Invoke(streamingMessage) ?? Task.CompletedTask);
                                lastUpdateTime = now;
                            }
                        }
                    }

                    _groupChat.AddChatMessage(new ChatMessageContent(AuthorRole.Assistant, streamingMessage.Content, agent.Name));
                }
                else
                {
                    var history = await historyBuilder.BuildChatHistoryAsync(Messages, kernel, cancellationToken);
                    var chatService = kernel.GetRequiredService<IChatCompletionService>();
                    await foreach (var content in chatService.GetStreamingChatMessageContentsAsync(history, promptExecutionSettings, kernel, cancellationToken))
                    {
                        await Task.Yield();
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!string.IsNullOrEmpty(content.Content))
                        {
                            streamingMessage.Append(content.Content);
                            approximateTokenCount++;
                            DateTime now = DateTime.Now;
                            if ((now - lastUpdateTime).TotalMilliseconds >= updateIntervalMs)
                            {
                                await (MessageUpdated?.Invoke(streamingMessage) ?? Task.CompletedTask);
                                lastUpdateTime = now;
                            }
                        }
                    }
                }
            }
            finally
            {
                kernel.FunctionInvocationFilters.Remove(trackingFilter);
            }

            await (MessageUpdated?.Invoke(streamingMessage) ?? Task.CompletedTask);

            TimeSpan processingTime = DateTime.Now - startTime;
            var statistics = _streamingManager.BuildStatistics(
                processingTime,
                chatConfiguration,
                approximateTokenCount,
                functionCalls.Select(fc => fc.Server).Distinct());
            var finalMessage = _streamingManager.CompleteStreaming(streamingMessage, statistics);
            await ReplaceStreamingMessageWithFinal(streamingMessage, finalMessage);
            _currentStreamingMessage = null;
        }
        catch (ModelDoesNotSupportToolsException ex)
        {
            logger.LogWarning(ex, "Model {ModelName} does not support function calling", chatConfiguration.ModelName);
            RemoveStreamingMessage(streamingMessage);
            _currentStreamingMessage = null;

            string errorMessage = chatConfiguration.Functions.Any()
                ? $"⚠️ The model **{chatConfiguration.ModelName}** does not support function calling. Please either:\n\n" +
                  "• Switch to a model that supports function calling\n" +
                  "• Disable all functions for this conversation\n\n" +
                  "You can see which models support function calling on the Models page."
                : $"⚠️ The model **{chatConfiguration.ModelName}** does not support the requested functionality.";

            await HandleError(errorMessage);
        }
        catch (Exception ex)
        {
            string errorPrefix = chatConfiguration.UseAgentResponses ? "Agent Error" : "Error";
            logger.LogError(ex, "Error processing {ResponseType} response", responseType);
            RemoveStreamingMessage(streamingMessage);
            _currentStreamingMessage = null;
            await HandleError($"{errorPrefix}: {ex.Message}");
        }
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
        _currentStreamingMessage = null;
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

/// <summary>
/// Filter for tracking invoked MCP servers during a chat session.
/// </summary>
file sealed class FunctionCallRecordingFilter(List<FunctionCallRecord> records) : IFunctionInvocationFilter
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
