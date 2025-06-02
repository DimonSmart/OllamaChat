using ChatClient.Shared.Models;
using Microsoft.Extensions.AI;
using System.Collections.ObjectModel;
using ChatClient.Api.Services;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using ChatClient.Shared.Services;

namespace ChatClient.Api.Client.Services;

public class ChatService(
    KernelService kernelService, 
    ILogger<ChatService> logger,
    IUserSettingsService userSettingsService) : IChatService
{
    private CancellationTokenSource? _cancellationTokenSource;
    private StreamingMessageManager? _streamingManager;
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

    public void Cancel()
    {
        _cancellationTokenSource?.Cancel();
        UpdateLoadingState(false);
    }

    public async Task AddUserMessageAndAnswerAsync(string text, IReadOnlyCollection<string> selectedFunctions, string modelName)
    {
        if (string.IsNullOrWhiteSpace(text) || IsLoading) return;

        await AddMessageAsync(new AppChatMessage(text, DateTime.Now, ChatRole.User, string.Empty));
        UpdateLoadingState(true);

        _cancellationTokenSource = new CancellationTokenSource();
        try
        {
            await ProcessAIResponseAsync(selectedFunctions, modelName, _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            await HandleError("Operation cancelled.");
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
    }    private async Task ProcessAIResponseAsync(IReadOnlyCollection<string> functionNames, string modelName, CancellationToken cancellationToken)
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
        await AddMessageAsync(streamingMessage);

        // Simple throttling for UI updates - no more than once every 500ms
        var lastUpdateTime = DateTime.MinValue;
        const int updateIntervalMs = 500;
        var approximateTtokenCount = 0;

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
                if (!string.IsNullOrEmpty(content.Content))
                {
                    streamingMessage.Append(content.Content);
                    approximateTtokenCount++;
                    
                    // Update UI no more than once every 500ms
                    var now = DateTime.Now;
                    if ((now - lastUpdateTime).TotalMilliseconds >= updateIntervalMs)
                    {
                        await (MessageUpdated?.Invoke(streamingMessage) ?? Task.CompletedTask);
                        lastUpdateTime = now;
                    }
                }
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
                settings.ShowTokensPerSecond ? approximateTtokenCount : null);
            
            var finalMessage = _streamingManager.CompleteStreaming(streamingMessage, statistics);
            
            // Replace streaming message with final message
            await ReplaceStreamingMessageWithFinal(streamingMessage, finalMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing AI response");
            
            // Remove streaming message on error
            RemoveStreamingMessage(streamingMessage);
            await HandleError($"Error: {ex.Message}");
        }
    }

    private ChatHistory BuildChatHistory()
    {
        var chatHistory = new ChatHistory();
        var roleHandlers = new Dictionary<ChatRole, Action<ChatHistory, string>>
        {
            { ChatRole.System, (history, content) => history.AddSystemMessage(content) },
            { ChatRole.User, (history, content) => history.AddUserMessage(content) },
            { ChatRole.Assistant, (history, content) => history.AddAssistantMessage(content) }
        };

        foreach (var message in Messages.Where(m => !m.IsStreaming))
        {
            if (roleHandlers.TryGetValue(message.Role, out var handler))            {
                handler(chatHistory, message.Content);
            }
        }
        
        return chatHistory;
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
        await AddMessageAsync(new AppChatMessage(text, DateTime.Now, ChatRole.System));
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
}
