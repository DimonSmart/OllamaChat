using ChatClient.Api.Client.Utils;
using ChatClient.Shared.Models;
using Microsoft.Extensions.AI;
using System.Collections.ObjectModel;
using ChatClient.Api.Services;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ChatClient.Api.Client.Services;

public class ChatService(KernelService kernelService, ILogger<ChatService> logger) : IChatService
{
    private CancellationTokenSource? _cancellationTokenSource;
    public event Action<bool>? LoadingStateChanged;
    public event Action? ChatInitialized;
    public event Func<IAppChatMessage, Task>? MessageAdded;
    public event Func<IAppChatMessage, Task>? MessageUpdated;
    public bool IsLoading { get; private set; }
    public ObservableCollection<IAppChatMessage> Messages { get; } = [];

    public void InitializeChat(SystemPrompt? initialPrompt)
    {
        Messages.Clear();
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
    }

    private async Task ProcessAIResponseAsync(IReadOnlyCollection<string> functionNames, string modelName, CancellationToken cancellationToken)
    {
        var startTime = DateTime.Now;
        logger.LogInformation("Processing AI response with model: {ModelName}", modelName);

        using var messageDebouncer = new Debouncer<IAppChatMessage>(
            async message => await (MessageUpdated?.Invoke(message) ?? Task.CompletedTask),
            TimeSpan.FromMilliseconds(250));
        
        var tempMsg = new StreamingAppChatMessage(string.Empty, DateTime.Now, ChatRole.Assistant);
        await AddMessageAsync(tempMsg);

        try
        {            // Build chat history from current messages
            var chatHistory = new ChatHistory();
            var roleHandlers = new Dictionary<ChatRole, Action<ChatHistory, string>>
            {
                { ChatRole.System, (history, content) => history.AddSystemMessage(content) },
                { ChatRole.User, (history, content) => history.AddUserMessage(content) },
                { ChatRole.Assistant, (history, content) => history.AddAssistantMessage(content) }
            };

            foreach (var message in Messages.Where(m => m != tempMsg))
            {
                if (roleHandlers.TryGetValue(message.Role, out var handler))
                {
                    handler(chatHistory, message.Content);
                }
            }

            // Create kernel and get chat completion service
            var kernel = await kernelService.CreateKernelAsync(modelName, functionNames);
            var chatService = kernel.GetRequiredService<IChatCompletionService>();

            var executionSettings = new PromptExecutionSettings
            {
                FunctionChoiceBehavior = (functionNames != null && functionNames.Any())
                    ? FunctionChoiceBehavior.Auto()
                    : FunctionChoiceBehavior.None()
            };

            // Stream the AI response
            await foreach (var content in chatService.GetStreamingChatMessageContentsAsync(
                chatHistory,
                executionSettings,
                kernel,
                cancellationToken: cancellationToken))
            {
                if (!string.IsNullOrEmpty(content.Content))
                {
                    tempMsg.Append(content.Content);
                    messageDebouncer.Enqueue(tempMsg);
                }
            }

            var processingTime = DateTime.Now - startTime;
            var stats = $"\n\n---\n" +
                       $"Processing time: {processingTime.TotalSeconds:F2} seconds\n" +
                       $"Model: {modelName}\n" +
                       $"Functions: {string.Join(", ", functionNames ?? [])}";
            tempMsg.SetStatistics(stats);

            // Replace streaming message with final message
            var finalMessage = new AppChatMessage(tempMsg.Content, tempMsg.MsgDateTime, ChatRole.Assistant);
            var index = Messages.IndexOf(tempMsg);
            if (index >= 0)
            {
                Messages[index] = finalMessage;
                await (MessageUpdated?.Invoke(finalMessage) ?? Task.CompletedTask);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing AI response");
            await HandleError($"Error: {ex.Message}");
        }
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
