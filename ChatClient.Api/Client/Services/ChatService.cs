using ChatClient.Api.Client.Utils;
using ChatClient.Shared.Models;
using Microsoft.Extensions.AI;
using System.Collections.ObjectModel;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChatClient.Api.Client.Services;

public class ChatService(HttpClient client) : IChatService
{
    private CancellationTokenSource? _cancellationTokenSource;
    public event Action<bool>? LoadingStateChanged;    public event Action? ChatInitialized;
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
            await ProcessStreamingResponseAsync(selectedFunctions, modelName, _cancellationTokenSource.Token);
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

    private async Task ProcessStreamingResponseAsync(IReadOnlyCollection<string> functionNames, string modelName, CancellationToken cancellationToken)
    {
        var readCallsCount = 0;
        var messageEventsCount = 0;
        var emptyMessagesCount = 0;
        var jsonParseErrorsCount = 0;
        var contentChunksCount = 0;
        var startTime = DateTime.Now;

        // Log diagnostic information
        Console.WriteLine($"ChatService using HttpClient with BaseAddress: {client.BaseAddress}");

        using var messageDebouncer = new Debouncer<IAppChatMessage>(
            async message => await (MessageUpdated?.Invoke(message) ?? Task.CompletedTask),
            TimeSpan.FromMilliseconds(250));
        using var request = CreateHttpRequest(functionNames, modelName);
        var tempMsg = new StreamingAppChatMessage(string.Empty, DateTime.Now, ChatRole.Assistant);
        await AddMessageAsync(tempMsg);

        // Send request with error handling        
        HttpResponseMessage response;
        try
        {
            Console.WriteLine($"Sending request to: {request.RequestUri}");
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            Console.WriteLine($"Got response with status code: {(int)response.StatusCode} {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"Error response content: {errorContent}");
                await HandleError($"Error {(int)response.StatusCode}: {response.ReasonPhrase}");
                return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception while sending request: {ex.Message}");
            await HandleError($"Exception: {ex.Message}");
            return;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        Task<string?>? readTask = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (readTask == null)
            {
                readCallsCount++;
                readTask = reader.ReadLineAsync(cancellationToken).AsTask();
            }

            var timeoutTask = Task.Delay(500, cancellationToken);
            var completed = await Task.WhenAny(readTask, timeoutTask);

            if (!readTask.IsCompleted)
            {
                await Task.Yield();
                continue;
            }

            var line = await readTask;
            readTask = null;

            if (line == null)
            {
                break;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                emptyMessagesCount++;
                await Task.Yield();
                messageDebouncer.Enqueue(tempMsg);
                continue;
            }

            if (!line.StartsWith("data: "))
            {
                await Task.Yield();
                messageDebouncer.Enqueue(tempMsg);
                continue;
            }

            var payload = line["data: ".Length..];
            payload = payload.Trim('\'');
            if (payload == "[DONE]")
            {
                break;
            }

            try
            {
                var dr = JsonSerializer.Deserialize<StreamResponse>(payload);
                if (dr?.Content is not null)
                {
                    contentChunksCount++;
                    tempMsg.Append(dr?.Content);
                    await Task.Yield();
                    messageDebouncer.Enqueue(tempMsg);
                    messageEventsCount++;
                }
                if (dr?.Error is not null)
                {
                    contentChunksCount++;
                    tempMsg.Append(dr?.Error);
                    await Task.Yield();
                    messageDebouncer.Enqueue(tempMsg);
                    messageEventsCount++;
                }

            }
            catch (JsonException)
            {
                jsonParseErrorsCount++;
            }
        }

        var processingTime = DateTime.Now - startTime; var stats = $"\n\n---\n" +
                    "Stream statistics:\n" +
                    $"- Total processing time: {processingTime.TotalSeconds:F2} seconds\n" +
                    $"- Stream read calls: {readCallsCount}\n" +
                    $"- Message events fired: {messageEventsCount}\n" +
                    $"- Content chunks received: {contentChunksCount}\n" +
                    $"- Empty messages: {emptyMessagesCount}\n" +
                    $"- JSON parse errors: {jsonParseErrorsCount}";
        tempMsg.SetStatistics(stats);

        // Replace the tempMsg (of type StreamingAppChatMessage) with a new message of type AppChatMessage
        var index = Messages.IndexOf(tempMsg);
        if (index == -1) throw new Exception("Invalid state exception");
        var finalMessage = new AppChatMessage(tempMsg);
        Messages[index] = finalMessage;
        messageDebouncer.ClearDelayedCalls();
        messageDebouncer.Enqueue(finalMessage);
    }

    private HttpRequestMessage CreateHttpRequest(IReadOnlyCollection<string> functionNames, string modelName)
    {
        var messages = Messages
            .Select(message => new AppChatMessage(message.Content, message.MsgDateTime, message.Role))
            .ToList();
        // Create API request with absolute URI to ensure it goes to the correct endpoint
        string requestUrl = "api/chat/stream";
        Console.WriteLine($"Creating request to URL: {requestUrl}");

        var request = new HttpRequestMessage(HttpMethod.Post, requestUrl)
        {
            Content = JsonContent.Create(new AppChatRequest
            {
                Messages = messages,                FunctionNames = functionNames.ToList(),
                ModelName = modelName
            })
        };
        request.Headers.Add("X-Response-Timeout", "600");
        request.Headers.Add("Keep-Alive", "timeout=600");
        request.Headers.Add("Transfer-Encoding", "chunked");
        return request;
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

    private class StreamResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("content")]
        public string? Content { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
