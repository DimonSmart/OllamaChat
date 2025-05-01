using ChatClient.Shared.Models;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Microsoft.Extensions.AI;
using System.Collections.ObjectModel;
using System.Net.Http.Json;
using System.Text.Json;


namespace ChatClient.Client.Services;

public class ChatService
{
    private readonly HttpClient _client;
    private CancellationTokenSource? _cancellationTokenSource;

    public event Action<bool>? LoadingStateChanged;
    public event Action? ChatInitialized;
    public event Func<Task>? MessageReceived;
    public event Action? ErrorOccurred;

    public bool IsLoading { get; private set; }
    public ObservableCollection<IAppChatMessage> Messages { get; } = new();

    public ChatService(HttpClient client)
    {
        _client = client;
    }

    public void InitializeChat(SystemPrompt? initialPrompt)
    {
        Messages.Clear();
        if (initialPrompt != null)
        {
            var systemMessage = new Shared.Models.AppChatMessage(initialPrompt.Content, DateTime.Now, ChatRole.System);
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

    public async Task SendMessageAsync(string text, List<string> selectedFunctions)
    {
        if (string.IsNullOrWhiteSpace(text) || IsLoading)
        {
            return;
        }

        AddMessage(new AppChatMessage(text, DateTime.Now, ChatRole.User));
        UpdateLoadingState(true);

        _cancellationTokenSource = new CancellationTokenSource();
        try
        {
            await ProcessStreamingResponseAsync(selectedFunctions, _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            HandleSystemMessage("Operation cancelled.");
        }
        catch (Exception ex)
        {
            HandleSystemMessage($"An error occurred while getting the response: {ex.Message}");
        }
        finally
        {
            Cleanup();
        }
    }

    private void AddMessage(IAppChatMessage message)
    {
        Messages.Add(message);
        MessageReceived?.Invoke();
    }    private async Task ProcessStreamingResponseAsync(List<string> functionNames, CancellationToken cancellationToken)
    {
        // Statistics counters
        int readCallsCount = 0;
        int messageEventsCount = 0;
        int emptyMessagesCount = 0;
        int jsonParseErrorsCount = 0;
        int contentChunksCount = 0;
        DateTime startTime = DateTime.Now;
        
        using var request = CreateHttpRequest(functionNames);
        var tempMsg = new StreamingAppChatMessage(string.Empty, DateTime.Now, ChatRole.Assistant);
        AddMessage(tempMsg);

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            HandleSystemMessage($"Error: {response.ReasonPhrase}");
            return;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            readCallsCount++;
            var lineTask = reader.ReadLineAsync(cancellationToken).AsTask();
            var timeoutTask = Task.Delay(50, cancellationToken);

            var completedTask = await Task.WhenAny(lineTask, timeoutTask);

            if (!lineTask.IsCompleted)
            {
                await Task.Yield();
                continue;
            }

            var line = await lineTask;

            // End of stream reached
            if (line == null)
            {
                break;
            }

            if (string.IsNullOrEmpty(line))
            {
                emptyMessagesCount++;
                await Task.Yield();
                continue;
            }
            
            if (!line.StartsWith("data: "))
            {
                await Task.Yield();
                continue;
            }

            var json = line["data: ".Length..];
            if (json == "[DONE]")
            {
                break;
            }

            try
            {
                var chunk = JsonSerializer
                    .Deserialize<StreamResponse>(json)?
                    .Content;

                contentChunksCount++;
                tempMsg.Append(chunk);
                await Task.Yield();
                MessageReceived?.Invoke();
                messageEventsCount++;
            }
            catch (JsonException)
            {
                jsonParseErrorsCount++;
            }
        }
        
        // Calculate total time
        TimeSpan processingTime = DateTime.Now - startTime;
        
        // Append statistics to the message
        string stats = $"\n\n---\n" +
                       $"Stream statistics:\n" +
                       $"- Total processing time: {processingTime.TotalSeconds:F2} seconds\n" +
                       $"- Stream read calls: {readCallsCount}\n" +
                       $"- Message events fired: {messageEventsCount}\n" +
                       $"- Content chunks received: {contentChunksCount}\n" +
                       $"- Empty messages: {emptyMessagesCount}\n" +
                       $"- JSON parse errors: {jsonParseErrorsCount}";
                       
        tempMsg.Append(stats);
        MessageReceived?.Invoke();
    }


    private HttpRequestMessage CreateHttpRequest(List<string> functionNames)
    {
        var messages = Messages
            .Select(message => new AppChatMessage(message.Content, message.MsgDateTime, message.Role))
            .ToList();

        var request = new HttpRequestMessage(HttpMethod.Post, "api/chat/stream")
        {
            Content = JsonContent.Create(new AppChatRequest { Messages = messages, FunctionNames = functionNames })
        };
        request.SetBrowserResponseStreamingEnabled(true);
        return request;
    }

    private void HandleSystemMessage(string text)
    {
        AddMessage(new Shared.Models.AppChatMessage(text, DateTime.Now, ChatRole.System));
        ErrorOccurred?.Invoke();
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
    }
}
