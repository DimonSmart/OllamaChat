﻿using System.Collections.ObjectModel;

using ChatClient.Api.Services;
using ChatClient.Shared.Models;

using Microsoft.Extensions.AI;

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

using OllamaSharp.Models.Exceptions;

using static MudBlazor.CategoryTypes;

namespace ChatClient.Api.Client.Services;

public class ChatService(
    KernelService kernelService,
    IChatHistoryBuilder historyBuilder,
    ILogger<ChatService> logger) : IChatService
{
    private CancellationTokenSource? _cancellationTokenSource;
    private StreamingMessageManager _streamingManager = null!;
    private StreamingAppChatMessage? _currentStreamingMessage;
    private SystemPrompt? _currentSystemPrompt;
    public event Action<bool>? LoadingStateChanged;
    public event Action? ChatInitialized;
    public event Func<IAppChatMessage, Task>? MessageAdded;
    public event Func<IAppChatMessage, Task>? MessageUpdated;
    public event Func<Guid, Task>? MessageDeleted;

    public bool IsLoading { get; private set; }
    public ObservableCollection<IAppChatMessage> Messages { get; } = [];
    public SystemPrompt? CurrentSystemPrompt => _currentSystemPrompt;

    public void InitializeChat(SystemPrompt? initialPrompt)
    {
        Messages.Clear();
        _streamingManager = new StreamingMessageManager(MessageUpdated);
        _currentSystemPrompt = initialPrompt;

        if (initialPrompt != null)
        {
            AppChatMessage systemMessage = new AppChatMessage(initialPrompt.Content, DateTime.Now, ChatRole.System, string.Empty);
            Messages.Add(systemMessage);
        }
        ChatInitialized?.Invoke();
    }

    public void ClearChat()
    {
        Messages.Clear();
        _currentSystemPrompt = null;
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
        UpdateLoadingState(true);

        _cancellationTokenSource = new CancellationTokenSource();
        try
        {
            await ProcessAIResponseAsync(chatConfiguration, trimmedText, _cancellationTokenSource.Token);
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
        string responseType = chatConfiguration.UseAgentMode ? "Agent" : "Ask";
        logger.LogInformation("Processing {ResponseType} response with model: {ModelName}", responseType, chatConfiguration.ModelName);

        List<FunctionCallRecord> functionCalls = [];
        StreamingAppChatMessage streamingMessage = _streamingManager.CreateStreamingMessage(functionCalls);
        _currentStreamingMessage = streamingMessage;
        await AddMessageAsync(streamingMessage);

        // Simple throttling for UI updates - no more than once every 500ms
        var lastUpdateTime = DateTime.MinValue;
        const int updateIntervalMs = 500;
        var approximateTokenCount = 0;

        try
        {
            var kernel = await kernelService.CreateKernelAsync(
                chatConfiguration,
                chatConfiguration.AutoSelectFunctions ? userMessage : null,
                chatConfiguration.AutoSelectFunctions ? chatConfiguration.AutoSelectCount : null);
            var history = await historyBuilder.BuildChatHistoryAsync(Messages, kernel, cancellationToken);
            var chatService = kernel.GetRequiredService<IChatCompletionService>();
            var promptExecutionSettings = new PromptExecutionSettings
            {
                FunctionChoiceBehavior = chatConfiguration.AutoSelectFunctions || chatConfiguration.Functions.Count != 0
                        ? FunctionChoiceBehavior.Auto()
                        : FunctionChoiceBehavior.None()
            };

            var streamingContent = chatConfiguration.UseAgentMode
                ? AgentService.GetAgentStreamingResponseAsync(history, promptExecutionSettings, kernel, cancellationToken)
                : chatService.GetStreamingChatMessageContentsAsync(history, promptExecutionSettings, kernel, cancellationToken);
            var trackingFilter = new FunctionCallRecordingFilter(functionCalls);

            string? doneReason = null;
            try
            {
                kernel.FunctionInvocationFilters.Add(trackingFilter);
                await foreach (var content in streamingContent)
                {
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!string.IsNullOrEmpty(content.Content))
                    {
                        streamingMessage.Append(content.Content);
                        approximateTokenCount++;
                        // Update UI no more than once every 500ms
                        DateTime now = DateTime.Now;
                        if ((now - lastUpdateTime).TotalMilliseconds >= updateIntervalMs)
                        {
                            await (MessageUpdated?.Invoke(streamingMessage) ?? Task.CompletedTask);
                            lastUpdateTime = now;
                        }
                    }

                    if (content.InnerContent is OllamaSharp.Models.Chat.ChatDoneResponseStream done && done.Done)
                        doneReason = done.DoneReason;

                    await Task.Yield();
                }

            }
            finally
            {
                kernel.FunctionInvocationFilters.Remove(trackingFilter);
            }

            await (MessageUpdated?.Invoke(streamingMessage) ?? Task.CompletedTask);

            var startTime = DateTime.Now;
            // Create statistics and complete streaming
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
            string errorPrefix = chatConfiguration.UseAgentMode ? "Agent Error" : "Error";
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
