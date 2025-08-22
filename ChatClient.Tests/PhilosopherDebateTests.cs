using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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

using Xunit;
using Xunit.Abstractions;

#pragma warning disable SKEXP0110
#pragma warning disable SKEXP0001
namespace ChatClient.Tests;

public partial class PhilosopherDebateTests
{
    private readonly ITestOutputHelper _output;

    public PhilosopherDebateTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task KantAndBentham_DebateLyingToSaveLife()
    {
        if (!await IsOllamaAvailableAsync())
        {
            _output.WriteLine("Ollama server unavailable, skipping test.");
            return;
        }

        ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            builder.AddConsole()
                   .AddDebug()
                   .SetMinimumLevel(LogLevel.Debug));

        using HttpClient httpClient = new() { BaseAddress = new Uri("http://localhost:11434") };
        var reducer = new AppForceLastUserReducer(loggerFactory.CreateLogger<AppForceLastUserReducer>());
        HttpChatCompletionService service = new(httpClient, _output);
        var wrappedService = new AppForceLastUserChatCompletionService(service, reducer);

        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IChatCompletionService>(wrappedService);
        builder.Services.AddSingleton(loggerFactory);
        builder.Services.AddLogging();
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
            HistoryReducer = reducer
        };

        ChatCompletionAgent bentham = new()
        {
            Name = "Bentham",
            Description = "Jeremy Bentham",
            Instructions = benthamDesc.Content,
            Kernel = kernel,
            HistoryReducer = reducer
        };

        GroupChatOrchestration chat = new(
            new RoundRobinGroupChatManager { MaximumInvocationCount = 8 },
            kant,
            bentham);

        await using InProcessRuntime runtime = new();
        await runtime.StartAsync();

        Microsoft.SemanticKernel.Agents.Orchestration.OrchestrationResult<string> result = await chat.InvokeAsync("Is it morally acceptable to lie to save a life?", runtime);
        string finalResult = await result.GetValueAsync(TimeSpan.FromSeconds(30));

        Assert.False(string.IsNullOrWhiteSpace(finalResult));
    }

    private static async Task<bool> IsOllamaAvailableAsync()
    {
        if (Environment.GetEnvironmentVariable("RUN_OLLAMA_TESTS") != "1")
            return false;

        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(2) };
            using HttpResponseMessage response = await client.GetAsync("http://localhost:11434/api/tags");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
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

            _output.WriteLine($"Processing history: {chatHistory.Count()} messages");
            var messages = chatHistory.Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content });
            string payload = JsonSerializer.Serialize(new { model = "phi4", messages, options = new { }, stream = false, think = false, CustomHeaders = new { } });

            _output.WriteLine($"Sending payload: {payload}");

            HttpResponseMessage response = await _httpClient.PostAsync("/api/chat", new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken);

            _output.WriteLine($"Received response: {response.StatusCode}");

            return new[] { new ChatMessageContent(AuthorRole.Assistant, "Philosophical response from mock service", "assistant") };
        }

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _output.WriteLine($"GetStreamingChatMessageContentsAsync called with {chatHistory.Count()} messages");

            _output.WriteLine($"Processing history: {chatHistory.Count()} messages");
            var messages = chatHistory.Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content });
            string payload = JsonSerializer.Serialize(new { model = "phi4", messages, options = new { }, stream = true, think = false, CustomHeaders = new { } });

            _output.WriteLine($"Sending streaming payload: {payload}");

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            _output.WriteLine($"Received streaming response: {response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var chunk = JsonSerializer.Deserialize<OllamaChunk>(line);
                if (chunk?.Message?.Content is { Length: > 0 } text)
                    yield return new StreamingChatMessageContent(AuthorRole.Assistant, text, chunk.Message.Role ?? "assistant");

                if (chunk?.Done == true)
                    break;
            }
        }

        private sealed record OllamaChunk(OllamaMessage? Message, bool Done);
        private sealed record OllamaMessage(string? Role, string? Content);
    }
}
