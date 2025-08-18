using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

using ChatClient.Api.Client.Services;
using ChatClient.Shared.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0110
#pragma warning disable SKEXP0001
namespace ChatClient.Tests;

public class PhilosopherDebateTests
{
    [Fact]
    public async Task KantAndBentham_DebateLyingToSaveLife()
    {
        var handler = new StubOllamaHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var service = new HttpChatCompletionService(httpClient);
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(service);
        Kernel kernel = builder.Build();

        List<AgentDescription> descriptions = await LoadDescriptionsAsync();
        AgentDescription kantDesc = descriptions.First(a => a.AgentName == "Immanuel Kant");
        AgentDescription benthamDesc = descriptions.First(a => a.AgentName == "Jeremy Bentham");

        ChatCompletionAgent kant = new()
        {
            Name = "Kant",
            Description = "Immanuel Kant",
            Instructions = kantDesc.Content,
            Kernel = kernel,
            HistoryReducer = new ForceLastUserReducer()
        };

        ChatCompletionAgent bentham = new()
        {
            Name = "Bentham",
            Description = "Jeremy Bentham",
            Instructions = benthamDesc.Content,
            Kernel = kernel,
            HistoryReducer = new ForceLastUserReducer()
        };

        GroupChatOrchestration chat = new(
            new RoundRobinGroupChatManager { MaximumInvocationCount = 2 },
            kant,
            bentham);

        await using InProcessRuntime runtime = new();
        await runtime.StartAsync();

        var result = await chat.InvokeAsync("Is it morally acceptable to lie to save a life?", runtime);
        await result.GetValueAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, handler.ObservedRoles.Count);
        Assert.All(handler.ObservedRoles, r => Assert.Equal("user", r));
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
                continue;

            await using FileStream stream = File.OpenRead(path);
            List<AgentDescription>? descriptions = await JsonSerializer.DeserializeAsync<List<AgentDescription>>(stream);
            return descriptions ?? [];
        }

        throw new FileNotFoundException($"Could not find agent_descriptions.json in any of the expected locations. Current dir: {currentDir}");
    }

    private sealed class HttpChatCompletionService(HttpClient httpClient) : IChatCompletionService
    {
        private readonly HttpClient _httpClient = httpClient;
        private readonly ForceLastUserReducer _reducer = new();
        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            var reduced = await _reducer.ReduceAsync(chatHistory, cancellationToken) ?? chatHistory;
            var messages = reduced.Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content });
            var payload = JsonSerializer.Serialize(new { model = "phi4", messages, options = new { }, stream = false, think = false, CustomHeaders = new { } });
            await _httpClient.PostAsync("/api/chat", new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken);
            return new[] { new ChatMessageContent(AuthorRole.Assistant, "ok", "assistant") };
        }

        public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            StreamingChatMessageContent content = new(AuthorRole.Assistant, "ok") { AuthorName = "assistant" };
            return Stream(content);
        }

        private static async IAsyncEnumerable<StreamingChatMessageContent> Stream(StreamingChatMessageContent content)
        {
            yield return content;
            await Task.CompletedTask;
        }
    }

    private sealed class StubOllamaHandler : HttpMessageHandler
    {
        public List<string> ObservedRoles { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = await request.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(payload);
            var messages = doc.RootElement.GetProperty("messages");
            var last = messages.EnumerateArray().Last();
            ObservedRoles.Add(last.GetProperty("role").GetString()!);

            const string responseJson = "{\"model\":\"phi4\",\"created_at\":\"2024-01-01T00:00:00Z\",\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"done\":true}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
