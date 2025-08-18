using System.Net;
using System.Text;
using System.Text.Json;

using ChatClient.Api.Client.Services;
using ChatClient.Shared.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;

using Xunit.Abstractions;

#pragma warning disable SKEXP0110
#pragma warning disable SKEXP0001
namespace ChatClient.Tests;

public class PhilosopherDebateTests
{
    private readonly ITestOutputHelper _output;

    public PhilosopherDebateTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task KantAndBentham_DebateLyingToSaveLife_WithDetailedLogging()
    {
        ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            builder.AddConsole()
                   .AddDebug()
                   .SetMinimumLevel(LogLevel.Debug));

        StubOllamaHandler handler = new(_output);
        HttpClient httpClient = new(handler) { BaseAddress = new Uri("http://localhost") };
        HttpChatCompletionService service = new(httpClient, _output);

        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(service);
        builder.Services.AddSingleton(loggerFactory);
        builder.Services.AddLogging();
        Kernel kernel = builder.Build();

        List<AgentDescription> descriptions = await LoadDescriptionsAsync();
        AgentDescription kantDesc = descriptions.First(a => a.AgentName == "Immanuel Kant");
        AgentDescription benthamDesc = descriptions.First(a => a.AgentName == "Jeremy Bentham");

        _output.WriteLine("Creating Kant agent...");
        ChatCompletionAgent kant = new()
        {
            Name = "Kant",
            Description = "Immanuel Kant",
            Instructions = kantDesc.Content,
            Kernel = kernel,
            HistoryReducer = new ForceLastUserReducer()
        };

        _output.WriteLine("Creating Bentham agent...");
        ChatCompletionAgent bentham = new()
        {
            Name = "Bentham",
            Description = "Jeremy Bentham",
            Instructions = benthamDesc.Content,
            Kernel = kernel,
            HistoryReducer = new ForceLastUserReducer()
        };

        _output.WriteLine("Creating group chat orchestration...");
        GroupChatOrchestration chat = new(
            new RoundRobinGroupChatManager { MaximumInvocationCount = 8 },
            kant,
            bentham);

        _output.WriteLine("Starting runtime...");
        await using InProcessRuntime runtime = new();
        await runtime.StartAsync();

        _output.WriteLine("Invoking group chat with question...");
        Microsoft.SemanticKernel.Agents.Orchestration.OrchestrationResult<string> result = await chat.InvokeAsync("Is it morally acceptable to lie to save a life?", runtime);

        _output.WriteLine("Waiting for result...");
        string finalResult = await result.GetValueAsync(TimeSpan.FromSeconds(30));

        _output.WriteLine($"Final result received. Handler observed {handler.ObservedRoles.Count} roles");
        _output.WriteLine($"Handler observed messages: {string.Join(", ", handler.ObservedMessages)}");

        // Print all observed interactions
        for (int i = 0; i < handler.ObservedRoles.Count; i++)
        {
            _output.WriteLine($"Message {i + 1}: Role={handler.ObservedRoles[i]}, Content={handler.ObservedMessages[i]}");
        }

        Assert.Equal(8, handler.ObservedRoles.Count);
        Assert.All(handler.ObservedRoles, r => Assert.Equal("user", r));
    }

    [Fact]
    public void TestStreamingMessageFlow_WithDetailedLogging()
    {
        // This test simulates the streaming flow to identify where the issue occurs
        StreamingOllamaHandler handler = new(_output);
        HttpClient httpClient = new(handler) { BaseAddress = new Uri("http://localhost:11434") };

        _output.WriteLine("Creating mock streaming scenario...");

        // Simulate the streaming flow that happens in the real app
        StreamingAppChatMessage streamingMessage = new(string.Empty, DateTime.Now, Microsoft.Extensions.AI.ChatRole.Assistant, agentName: "PlaceholderAgent");

        _output.WriteLine($"Initial streaming message - Agent: {streamingMessage.AgentName}, Content: '{streamingMessage.Content}'");

        // Simulate what happens when the real agent name is determined
        streamingMessage.SetAgentName("Philosopher");
        streamingMessage.ResetContent();

        _output.WriteLine($"After SetAgentName and ResetContent - Agent: {streamingMessage.AgentName}, Content: '{streamingMessage.Content}'");

        // Simulate streaming tokens
        string[] tokens = new[] { "Lying", " is", " a", " complex", " moral", " issue", "..." };

        foreach (string? token in tokens)
        {
            streamingMessage.Append(token);
            _output.WriteLine($"After appending '{token}' - Content: '{streamingMessage.Content}', Length: {streamingMessage.Content.Length}");
        }

        _output.WriteLine($"Final streaming message - Agent: {streamingMessage.AgentName}, Content: '{streamingMessage.Content}'");

        // Verify the streaming message was built correctly
        Assert.Equal("Philosopher", streamingMessage.AgentName);
        Assert.NotEmpty(streamingMessage.Content);
        Assert.Contains("complex", streamingMessage.Content);
    }

    [Fact]
    public async Task TestMessageUpdatedEventFlow_WithDetailedLogging()
    {
        // Test that MessageUpdated event is properly called during streaming
        _output.WriteLine("Testing MessageUpdated event flow...");

        List<(string agentName, string content, bool isFinal)> messageUpdates = new();

        // Create streaming message
        StreamingAppChatMessage streamingMessage = new(string.Empty, DateTime.Now, Microsoft.Extensions.AI.ChatRole.Assistant, agentName: "TestAgent");

        // Simulate the MessageUpdated event handler (like in real UI)
        Func<StreamingAppChatMessage, bool, Task> messageUpdatedHandler = async (msg, isFinal) =>
        {
            _output.WriteLine($"MessageUpdated called - Agent: {msg.AgentName}, Content: '{msg.Content}', Final: {isFinal}, Content Length: {msg.Content.Length}");
            messageUpdates.Add((msg.AgentName ?? "null", msg.Content, isFinal));
            await Task.CompletedTask;
        };

        // Simulate streaming tokens with manual MessageUpdated calls
        string[] tokens = new[] { "Hello", " world", "!", " This", " is", " a", " test." };

        _output.WriteLine($"Starting with - Agent: {streamingMessage.AgentName}, Content: '{streamingMessage.Content}'");

        foreach (string? token in tokens)
        {
            streamingMessage.Append(token);

            // Manually call the event handler to simulate what ChatService does
            await messageUpdatedHandler(streamingMessage, false);
        }

        // Final update
        await messageUpdatedHandler(streamingMessage, true);

        _output.WriteLine($"Final state - Agent: {streamingMessage.AgentName}, Content: '{streamingMessage.Content}'");
        _output.WriteLine($"Total MessageUpdated calls: {messageUpdates.Count}");

        // Verify all updates were received
        Assert.Equal(8, messageUpdates.Count); // 7 tokens + 1 final
        Assert.All(messageUpdates.Take(7), update => Assert.False(update.isFinal));
        Assert.True(messageUpdates.Last().isFinal);

        // Verify content progression
        Assert.Equal("Hello", messageUpdates[0].content);
        Assert.Equal("Hello world", messageUpdates[1].content);
        Assert.Equal("Hello world!", messageUpdates[2].content);
        Assert.Equal("Hello world! This is a test.", messageUpdates.Last().content);

        // Verify agent name is consistent
        Assert.All(messageUpdates, update => Assert.Equal("TestAgent", update.agentName));

        _output.WriteLine("✅ All MessageUpdated events were received correctly");
    }

    private static async Task<List<AgentDescription>> LoadDescriptionsAsync()
    {
        string currentDir = Directory.GetCurrentDirectory();
        string[] paths =
        [
            Path.Combine("ChatClient.Api", "Data", "agent_descriptions.json"),
            Path.Combine("..", "..", "..", "..", "ChatClient.Api", "Data", "agent_descriptions.json"),
            Path.Combine("c:", "Private", "OllamaChat", "ChatClient.Api", "Data", "agent_descriptions.json"),
            Path.GetFullPath(Path.Combine(currentDir, "..", "..", "..", "..", "ChatClient.Api", "Data", "agent_descriptions.json"))
        ];

        foreach (string path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            await using FileStream stream = File.OpenRead(path);
            List<AgentDescription>? descriptions = await JsonSerializer.DeserializeAsync<List<AgentDescription>>(stream);
            return descriptions ?? [];
        }

        throw new FileNotFoundException($"Could not find agent_descriptions.json in any of the expected locations. Current dir: {currentDir}");
    }

    private sealed class HttpChatCompletionService : IChatCompletionService
    {
        private readonly HttpClient _httpClient;
        private readonly ForceLastUserReducer _reducer = new();
        private readonly ITestOutputHelper _output;

        public HttpChatCompletionService(HttpClient httpClient, ITestOutputHelper output)
        {
            _httpClient = httpClient;
            _output = output;
        }

        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            _output.WriteLine($"GetChatMessageContentsAsync called with {chatHistory.Count()} messages");

            IEnumerable<ChatMessageContent> reduced = await _reducer.ReduceAsync(chatHistory, cancellationToken) ?? chatHistory;
            _output.WriteLine($"After reduction: {reduced.Count()} messages");

            var messages = reduced.Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content });
            string payload = JsonSerializer.Serialize(new { model = "phi4", messages, options = new { }, stream = false, think = false, CustomHeaders = new { } });

            _output.WriteLine($"Sending payload: {payload}");

            HttpResponseMessage response = await _httpClient.PostAsync("/api/chat", new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken);

            _output.WriteLine($"Received response: {response.StatusCode}");

            return new[] { new ChatMessageContent(AuthorRole.Assistant, "Philosophical response from mock service", "assistant") };
        }
    }

    private sealed class StubOllamaHandler : HttpMessageHandler
    {
        public List<string> ObservedRoles { get; } = [];
        public List<string> ObservedMessages { get; } = [];
        private readonly ITestOutputHelper _output;

        public StubOllamaHandler(ITestOutputHelper output)
        {
            _output = output;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _output.WriteLine($"StubOllamaHandler.SendAsync called: {request.Method} {request.RequestUri}");

            if (request.Content == null)
            {
                _output.WriteLine("Request content is null");
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            string payload = await request.Content.ReadAsStringAsync(cancellationToken);
            _output.WriteLine($"Request payload: {payload}");

            using JsonDocument doc = JsonDocument.Parse(payload);
            JsonElement messages = doc.RootElement.GetProperty("messages");
            List<JsonElement> messagesArray = messages.EnumerateArray().ToList();

            _output.WriteLine($"Found {messagesArray.Count} messages in payload");

            if (messagesArray.Count > 0)
            {
                JsonElement last = messagesArray.Last();
                string role = last.GetProperty("role").GetString()!;
                string content = last.GetProperty("content").GetString() ?? "";

                ObservedRoles.Add(role);
                ObservedMessages.Add(content);

                _output.WriteLine($"Last message - Role: {role}, Content: {content}");
            }

            // Simulate streaming response
            const string responseJson = "{\"model\":\"phi4\",\"created_at\":\"2024-01-01T00:00:00Z\",\"message\":{\"role\":\"assistant\",\"content\":\"Philosophical response\"},\"done\":true}";

            _output.WriteLine($"Returning response: {responseJson}");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StreamingOllamaHandler : HttpMessageHandler
    {
        private readonly ITestOutputHelper _output;

        public StreamingOllamaHandler(ITestOutputHelper output)
        {
            _output = output;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _output.WriteLine($"StreamingOllamaHandler.SendAsync called: {request.Method} {request.RequestUri}");

            // Simulate streaming response with multiple chunks
            string streamingResponse = """
{"model":"qwen3:latest","created_at":"2025-08-18T17:21:01.0384422Z","message":{"role":"assistant","content":"Lying"},"done":false}
{"model":"qwen3:latest","created_at":"2025-08-18T17:21:01.4095154Z","message":{"role":"assistant","content":" is"},"done":false}
{"model":"qwen3:latest","created_at":"2025-08-18T17:21:01.7655802Z","message":{"role":"assistant","content":" a"},"done":false}
{"model":"qwen3:latest","created_at":"2025-08-18T17:21:02.0925197Z","message":{"role":"assistant","content":" complex"},"done":false}
{"model":"qwen3:latest","created_at":"2025-08-18T17:21:02.3664196Z","message":{"role":"assistant","content":" moral"},"done":false}
{"model":"qwen3:latest","created_at":"2025-08-18T17:21:02.6360194Z","message":{"role":"assistant","content":" issue"},"done":false}
{"model":"qwen3:latest","created_at":"2025-08-18T17:21:02.9322536Z","message":{"role":"assistant","content":""},"done_reason":"stop","done":true}
""";

            _output.WriteLine($"Returning streaming response with {streamingResponse.Split('\n').Length} chunks");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(streamingResponse, Encoding.UTF8, "application/x-ndjson")
            });
        }
    }
}
