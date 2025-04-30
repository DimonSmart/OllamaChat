using System.Collections.ObjectModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatClient.Shared.Models;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Microsoft.Extensions.AI;

namespace ChatClient.Client.Services;

public class ChatService
{
    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _cts;

    public event Action<bool>? LoadingStateChanged;
    public event Action? ChatInitialized;
    public event Action? MessageReceived;
    public event Action? ErrorOccurred;
    
    public bool IsLoading { get; private set; }
    public ObservableCollection<Message> Messages { get; } = new();
    public List<Message> HistoryMessages { get; } = new();
    
    public ChatService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public void InitializeChat(SystemPrompt? prompt)
    {
        Messages.Clear();
        HistoryMessages.Clear();

        if (prompt != null)
        {
            var systemMessage = new Message(prompt.Content, DateTime.Now, ChatRole.System);
            HistoryMessages.Add(systemMessage);
        }
        
        ChatInitialized?.Invoke();
    }
    
    public void ClearChat()
    {
        Messages.Clear();
        HistoryMessages.Clear();
    }
    
    public void Cancel()
    {
        _cts?.Cancel();
        SetLoadingState(false);
    }
    
    public async Task SendMessageAsync(string messageText, List<string> selectedFunctions)
    {
        if (string.IsNullOrWhiteSpace(messageText) || IsLoading)
        {
            return;
        }

        var userMessage = new Message(messageText, DateTime.Now, ChatRole.User);
        Messages.Add(userMessage);
        HistoryMessages.Add(userMessage);

        SetLoadingState(true);
        MessageReceived?.Invoke();

        Message? assistantResponse = null;

        try
        {
            _cts = new CancellationTokenSource();

            var request = new HttpRequestMessage(HttpMethod.Post, "api/chat/stream")
            {
                Content = JsonContent.Create(new AppChatRequest { Messages = HistoryMessages, FunctionNames = selectedFunctions })
            };
            request.SetBrowserResponseStreamingEnabled(true);

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                _cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(_cts.Token);
                Messages.Add(new Message($"Sorry, an error occurred: {response.ReasonPhrase}", DateTime.Now, ChatRole.System));
                ErrorOccurred?.Invoke();
                return;
            }

            assistantResponse = new Message(string.Empty, DateTime.Now, ChatRole.Assistant);
            Messages.Add(assistantResponse);
            MessageReceived?.Invoke();

            using var stream = await response.Content.ReadAsStreamAsync(_cts.Token);
            using var reader = new StreamReader(stream);

            string? line;
            var messageBuilder = new StringBuilder();
            var firstChunkReceived = false;
            
            while ((line = await reader.ReadLineAsync(_cts.Token)) != null)
            {
                if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;

                var jsonData = line.Substring(6);
                if (jsonData == "[DONE]") break;

                try
                {
                    var data = JsonSerializer.Deserialize<StreamResponse>(jsonData);
                    if (data?.content != null && assistantResponse != null && !_cts.Token.IsCancellationRequested)
                    {
                        if (!firstChunkReceived)
                        {
                            SetLoadingState(false);
                            firstChunkReceived = true;
                        }
                        
                        messageBuilder.Append(data.content);
                        assistantResponse.Content = messageBuilder.ToString();
                        MessageReceived?.Invoke();
                    }
                }
                catch (JsonException)
                {
                    continue;
                }
            }

            if (!firstChunkReceived)
            {
                SetLoadingState(false);
            }

            if (assistantResponse != null && !string.IsNullOrWhiteSpace(assistantResponse.Content))
            {
                HistoryMessages.Add(assistantResponse);
            }
            else if (assistantResponse != null)
            {
                Messages.Remove(assistantResponse);
            }
        }
        catch (OperationCanceledException)
        {
            Messages.Add(new Message("Operation cancelled.", DateTime.Now, ChatRole.System));
            
            if (assistantResponse != null && !string.IsNullOrWhiteSpace(assistantResponse.Content))
            {
                HistoryMessages.Add(assistantResponse);
            }
            else if (assistantResponse != null)
            {
                Messages.Remove(assistantResponse);
            }
        }
        catch (Exception ex)
        {
            Messages.Add(new Message($"An error occurred while getting the response: {ex.Message}", DateTime.Now, ChatRole.System));
            
            if (assistantResponse != null)
            {
                Messages.Remove(assistantResponse);
            }
            
            ErrorOccurred?.Invoke();
        }
        finally
        {
            SetLoadingState(false);
            _cts?.Dispose();
            _cts = null;
            MessageReceived?.Invoke();
        }
    }
    
    private void SetLoadingState(bool loading)
    {
        IsLoading = loading;
        LoadingStateChanged?.Invoke(loading);
    }
    
    private class StreamResponse
    {
        public string? content { get; set; }
    }
}
