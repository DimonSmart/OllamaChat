using ChatClient.Shared.Models;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Microsoft.Extensions.AI;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;


namespace ChatClient.Client.Services
{
    public class ChatService
    {
        private readonly HttpClient _client;
        private CancellationTokenSource? _cancellationTokenSource;

        public event Action<bool>? LoadingStateChanged;
        public event Action? ChatInitialized;
        public event Action? MessageReceived;
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
            var request = CreateHttpRequest(functionNames);
            var temporaryMessageWhileReceiving = new StreamingAppChatMessage(string.Empty, DateTime.Now, ChatRole.Assistant);
            AddMessage(temporaryMessageWhileReceiving);

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var reason = response.ReasonPhrase;
                HandleSystemMessage($"Sorry, an error occurred: {reason}");
                return;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!cancellationToken.IsCancellationRequested && (await reader.ReadLineAsync(cancellationToken)) is string line)
            {
                if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                {
                    continue;
                }

                var jsonData = line["data: ".Length..];
                if (jsonData == "[DONE]")
                {
                    break;
                }

                try
                {
                    var chunk = JsonSerializer.Deserialize<StreamResponse>(jsonData)?.Content;
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        temporaryMessageWhileReceiving.Append(chunk);
                        MessageReceived?.Invoke();
                    }
                }
                catch (JsonException)
                {
                    // ignore
                }
            }
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
            UpdateLoadingState(false);
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
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
    }}
