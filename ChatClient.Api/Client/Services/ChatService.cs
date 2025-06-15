using System.Collections.ObjectModel;

using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using OllamaSharp.Models.Exceptions;

namespace ChatClient.Api.Client.Services;

public class ChatService(
    KernelService kernelService,
    ILogger<ChatService> logger,
    IUserSettingsService userSettingsService) : IChatService
{
    private CancellationTokenSource? _cancellationTokenSource;
    private StreamingMessageManager? _streamingManager;
    private StreamingAppChatMessage? _currentStreamingMessage;
    public event Action<bool>? LoadingStateChanged;
    public event Action? ChatInitialized;
    public event Func<IAppChatMessage, Task>? MessageAdded;
    public event Func<IAppChatMessage, Task>? MessageUpdated;

    public bool IsLoading { get; private set; }
    public ObservableCollection<IAppChatMessage> Messages { get; } = [];

    public void InitializeChat(SystemPrompt? initialPrompt)
    {
        Messages.Clear();
        _streamingManager = new StreamingMessageManager(MessageUpdated);

        if (initialPrompt != null)
        {
            var systemMessage = new AppChatMessage(initialPrompt.Content, DateTime.Now, ChatRole.System, string.Empty);
            Messages.Add(systemMessage);
        }
        ChatInitialized?.Invoke();
    }

    public void ClearChat()
    {
        Messages.Clear();
    }

    public async Task CancelAsync()
    {
        _cancellationTokenSource?.Cancel();

        // Handle the current streaming message if it exists
        if (_streamingManager != null && _currentStreamingMessage != null)
        {
            var canceledMessage = _streamingManager.CancelStreaming(_currentStreamingMessage);
            await ReplaceStreamingMessageWithFinal(_currentStreamingMessage, canceledMessage);
            _currentStreamingMessage = null;
        }

        UpdateLoadingState(false);
    }

    public async Task AddUserMessageAndAnswerAsync(string text, IReadOnlyCollection<string> selectedFunctions, string modelName, IReadOnlyList<ChatMessageFile>? files = null)
    {
        if (string.IsNullOrWhiteSpace(text) || IsLoading) return;

        await AddMessageAsync(new AppChatMessage(text, DateTime.Now, ChatRole.User, string.Empty, files));
        UpdateLoadingState(true);

        _cancellationTokenSource = new CancellationTokenSource();
        try
        {
            await ProcessAIResponseAsync(selectedFunctions, modelName, _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is already handled in Cancel() method
            // Just ensure cleanup happens
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

    private async Task ProcessAIResponseAsync(IReadOnlyCollection<string> functionNames, string modelName, CancellationToken cancellationToken)
    {
        var startTime = DateTime.Now;
        logger.LogInformation("Processing AI response with model: {ModelName}", modelName);

        if (_streamingManager == null)
        {
            logger.LogError("StreamingMessageManager is not initialized");
            await HandleError("Chat not properly initialized");
            return;
        }
        // Create streaming message
        var streamingMessage = _streamingManager.CreateStreamingMessage();
        _currentStreamingMessage = streamingMessage;
        await AddMessageAsync(streamingMessage);

        // Simple throttling for UI updates - no more than once every 500ms
        var lastUpdateTime = DateTime.MinValue;
        const int updateIntervalMs = 500;
        var approximateTokenCount = 0;

        try
        {
            var chatHistory = BuildChatHistory();
            // Create kernel and get chat completion service
            var kernel = await kernelService.CreateKernelAsync(modelName, functionNames);
            var chatService = kernel.GetRequiredService<IChatCompletionService>();

            var executionSettings = new PromptExecutionSettings
            {
                FunctionChoiceBehavior = (functionNames?.Any() == true)
                    ? FunctionChoiceBehavior.Auto()
                    : FunctionChoiceBehavior.None()
            };

            // Streaming response from LLM
            await foreach (var content in chatService.GetStreamingChatMessageContentsAsync(
                chatHistory,
                executionSettings,
                kernel,
                cancellationToken: cancellationToken))
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.IsNullOrEmpty(content.Content))
                {
                    streamingMessage.Append(content.Content);
                    approximateTokenCount++;

                    // Update UI no more than once every 500ms
                    var now = DateTime.Now;
                    if ((now - lastUpdateTime).TotalMilliseconds >= updateIntervalMs)
                    {
                        await (MessageUpdated?.Invoke(streamingMessage) ?? Task.CompletedTask);
                        lastUpdateTime = now;
                    }
                }
                await Task.Yield();
            }

            // Final update immediately after streaming completion
            await (MessageUpdated?.Invoke(streamingMessage) ?? Task.CompletedTask);

            // Create statistics and complete streaming
            var processingTime = DateTime.Now - startTime;
            var settings = await userSettingsService.GetSettingsAsync();
            var statistics = _streamingManager.BuildStatistics(
                processingTime,
                modelName,
                functionNames,
                settings.ShowTokensPerSecond ? approximateTokenCount : null);

            var finalMessage = _streamingManager.CompleteStreaming(streamingMessage, statistics);
            // Replace streaming message with final message
            await ReplaceStreamingMessageWithFinal(streamingMessage, finalMessage);
            _currentStreamingMessage = null;
        }
        catch (ModelDoesNotSupportToolsException ex)
        {
            logger.LogWarning(ex, "Model {ModelName} does not support function calling", modelName);
            RemoveStreamingMessage(streamingMessage);
            _currentStreamingMessage = null;
            
            var errorMessage = functionNames?.Any() == true 
                ? $"⚠️ The model **{modelName}** does not support function calling. Please either:\n\n" +
                  "• Switch to a model that supports function calling\n" +
                  "• Disable all functions for this conversation\n\n" +
                  "You can see which models support function calling on the Models page."
                : $"⚠️ The model **{modelName}** does not support the requested functionality.";
                
            await HandleError(errorMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing AI response");
            // Remove streaming message on error
            RemoveStreamingMessage(streamingMessage);
            _currentStreamingMessage = null;
            await HandleError($"Error: {ex.Message}");
        }
    }
    private ChatHistory BuildChatHistory()
    {
        var history = new ChatHistory();

        foreach (var msg in this.Messages.Where(m => !m.IsStreaming))
        {
            var items = new ChatMessageContentItemCollection();

            if (!string.IsNullOrEmpty(msg.Content))
            {
                items.Add(new Microsoft.SemanticKernel.TextContent(msg.Content));
            }

            // Add file attachments
            foreach (var file in msg.Files)
            {
                if (IsImageContentType(file.ContentType))
                {
                    items.Add(new ImageContent(new BinaryData(file.Data), file.ContentType));
                }
                else
                {
                    var fileDescription = $"File: {file.Name} ({file.ContentType})";
                    items.Add(new Microsoft.SemanticKernel.TextContent(fileDescription));
                }
            }

            var role = ConvertToAuthorRole(msg.Role);
            history.Add(new ChatMessageContent(role, items));
        }

        return history;
    }
    private static AuthorRole ConvertToAuthorRole(ChatRole chatRole)
    {
        if (chatRole == ChatRole.System)
            return AuthorRole.System;
        if (chatRole == ChatRole.Assistant)
            return AuthorRole.Assistant;

        return AuthorRole.User;
    }

    private static bool IsImageContentType(string contentType)
    {
        return contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ReplaceStreamingMessageWithFinal(StreamingAppChatMessage streamingMessage, AppChatMessage finalMessage)
    {
        var index = Messages.IndexOf(streamingMessage);

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
}
