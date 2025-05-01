using ChatClient.Shared.Models;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Microsoft.Extensions.AI;
using System.Collections.ObjectModel;
using System.Net.Http.Json;
using System.Text;
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
    }        
    
    private async Task ProcessStreamingResponseAsync(List<string> functionNames, CancellationToken cancellationToken)
    {
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

            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
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

                tempMsg.Append(chunk);
                await Task.Yield();
                MessageReceived?.Invoke();
            }
            catch (JsonException)
            {
                // Skip JSON parsing errors
            }
        }
    }


    private async Task ProcessStreamingResponseAsync1(List<string> functionNames, CancellationToken cancellationToken)
    {
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
        var sb = new StringBuilder();
        var buffer = new char[2048];

        while (!cancellationToken.IsCancellationRequested)
        {
            int read = await reader.ReadAsync(buffer, 0, buffer.Length);
            if (read == 0) break; // конец потока

            sb.Append(buffer, 0, read);

            // пока в буфере есть полное событие (разделитель "\n\n")
            int sep;
            while ((sep = sb.ToString().IndexOf("\n\n", StringComparison.Ordinal)) != -1)
            {
                var evt = sb.ToString(0, sep);
                sb.Remove(0, sep + 2);

                // каждое событие может состоять из нескольких строк
                foreach (var line in evt.Split('\n'))
                {
                    if (!line.StartsWith("data: ")) continue;
                    var json = line["data: ".Length..];
                    if (json == "[DONE]") return;

                    try
                    {
                        var chunk = JsonSerializer.Deserialize<StreamResponse>(json)?.Content;
                        if (chunk != null)
                        {
                            tempMsg.Append(chunk);
                            MessageReceived?.Invoke();
                        }
                    }
                    catch (JsonException)
                    {
                        // можно логировать некорректный json, если нужно
                    }
                }
            }
        }
    }


    private async Task ProcessStreamingResponseAsync2(List<string> fnames, CancellationToken ct)
    {
        var req = CreateHttpRequest(fnames);
        var temp = new StreamingAppChatMessage("", DateTime.Now, ChatRole.Assistant);
        AddMessage(temp);

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            HandleSystemMessage($"Error: {resp.ReasonPhrase}");
            return;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[4096];
        var sb = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var n = await reader.ReadAsync(buffer, 0, buffer.Length);
            if (n == 0) break;

            sb.Append(buffer, 0, n);

            while (TryExtractEvent(sb, out var evt))
            {
                if (ProcessEvent(evt, temp)) return;
            }
        }
    }

    // returns true if we saw [DONE] and should stop
    bool ProcessEvent(string evt, StreamingAppChatMessage temp)
    {
        foreach (var line in evt.Split('\n'))
        {
            if (!line.StartsWith("data: ")) continue;
            var json = line[6..];
            if (json == "[DONE]") return true;

            try
            {
                var chunk = JsonSerializer.Deserialize<StreamResponse>(json)?.Content;
                if (chunk is not null)
                {
                    temp.Append(chunk);
                    MessageReceived?.Invoke();
                }
            }
            catch
            {
                // malformed JSON — ignore
            }
        }
        return false;
    }

    // ищет первую пару "\n\n", вынимает блок до неё и удаляет из sb
    bool TryExtractEvent(StringBuilder sb, out string evt)
    {
        evt = null!;
        var idx = sb.IndexOf("\n\n");
        if (idx < 0) return false;

        evt = sb.ToString(0, idx);
        sb.Remove(0, idx + 2);
        return true;
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

static class SBExtensions
{
    public static int IndexOf(this StringBuilder sb, string pattern)
    {
        var len = pattern.Length;
        for (var i = 0; i + len <= sb.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < len; j++)
            {
                if (sb[i + j] != pattern[j])
                {
                    ok = false;
                    break;
                }
            }
            if (ok) return i;
        }
        return -1;
    }
}