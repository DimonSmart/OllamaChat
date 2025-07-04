using System.Collections.ObjectModel;

using ChatClient.Api.Services;
using ChatClient.Shared.Models;

using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

using OllamaSharp.Models.Exceptions;

namespace ChatClient.Api.Client.Services;

public class ChatService(
    KernelService kernelService,
    AgentService agentService,
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

        await AddMessageAsync(new AppChatMessage(text, DateTime.Now, ChatRole.User, string.Empty, files));
        UpdateLoadingState(true);

        _cancellationTokenSource = new CancellationTokenSource();
        try
        {
            await ProcessAIResponseAsync(chatConfiguration, _cancellationTokenSource.Token);
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

    private async Task<IAsyncEnumerable<StreamingChatMessageContent>> GetStreamingContentAsync(
        ChatConfiguration chatConfiguration,
        ChatHistory chatHistory,
        CancellationToken cancellationToken)
    {
        if (chatConfiguration.UseAgentMode)
        {
            string systemPrompt = Messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Content ?? "You are a helpful AI assistant.";
            ChatCompletionAgent agent = await agentService.CreateChatAgentAsync(chatConfiguration, systemPrompt);
            return agentService.GetAgentStreamingResponseAsync(agent, chatHistory, chatConfiguration, cancellationToken);
        }

        Kernel kernel = await kernelService.CreateKernelAsync(chatConfiguration);
        IChatCompletionService chatService = kernel.GetRequiredService<IChatCompletionService>();
        PromptExecutionSettings executionSettings = new PromptExecutionSettings
        {
            FunctionChoiceBehavior = chatConfiguration.Functions.Any()
                ? FunctionChoiceBehavior.Auto()
                : FunctionChoiceBehavior.None()
        };

        return chatService.GetStreamingChatMessageContentsAsync(
            chatHistory,
            executionSettings,
            kernel,
            cancellationToken: cancellationToken);
    }

    private async Task ProcessAIResponseAsync(ChatConfiguration chatConfiguration, CancellationToken cancellationToken)
    {
        DateTime startTime = DateTime.Now;
        string responseType = chatConfiguration.UseAgentMode ? "Agent" : "Ask";
        logger.LogInformation("Processing {ResponseType} response with model: {ModelName}", responseType, chatConfiguration.ModelName);

        StreamingAppChatMessage streamingMessage = _streamingManager.CreateStreamingMessage();
        _currentStreamingMessage = streamingMessage;
        await AddMessageAsync(streamingMessage);

        // Simple throttling for UI updates - no more than once every 500ms
        DateTime lastUpdateTime = DateTime.MinValue;
        const int updateIntervalMs = 500;
        int approximateTokenCount = 0;

        try
        {
            ChatHistory chatHistory = BuildChatHistory();
            IAsyncEnumerable<StreamingChatMessageContent> streamingContent = await GetStreamingContentAsync(chatConfiguration, chatHistory, cancellationToken);

            await foreach (StreamingChatMessageContent content in streamingContent)
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
                await Task.Yield();
            }

            await (MessageUpdated?.Invoke(streamingMessage) ?? Task.CompletedTask);

            // Create statistics and complete streaming
            TimeSpan processingTime = DateTime.Now - startTime;
            string statistics = _streamingManager.BuildStatistics(
                processingTime,
                chatConfiguration,
                approximateTokenCount);
            AppChatMessage finalMessage = _streamingManager.CompleteStreaming(streamingMessage, statistics);
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

    private ChatHistory BuildChatHistory()
    {
        ChatHistory history = new ChatHistory();

        foreach (IAppChatMessage? msg in this.Messages.Where(m => !m.IsStreaming))
        {
            ChatMessageContentItemCollection items = new ChatMessageContentItemCollection();

            if (!string.IsNullOrEmpty(msg.Content))
            {
                items.Add(new Microsoft.SemanticKernel.TextContent(msg.Content));
            }
            foreach (ChatMessageFile file in msg.Files)
            {
                if (IsImageContentType(file.ContentType))
                {
                    items.Add(new ImageContent(new BinaryData(file.Data), file.ContentType));
                }
                else
                {
                    string fileDescription = $"File: {file.Name} ({file.ContentType})";
                    items.Add(new Microsoft.SemanticKernel.TextContent(fileDescription));
                }
            }

            AuthorRole role = ConvertToAuthorRole(msg.Role);
            history.Add(new ChatMessageContent(role, items));
        }

        return history;
    }
    private static AuthorRole ConvertToAuthorRole(ChatRole chatRole)
    {
        if (chatRole == ChatRole.System)
        {
            return AuthorRole.System;
        }

        if (chatRole == ChatRole.Assistant)
        {
            return AuthorRole.Assistant;
        }

        return AuthorRole.User;
    }

    private static bool IsImageContentType(string contentType)
    {
        return contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
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
}
