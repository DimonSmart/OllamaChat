using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.IO;
using System.Linq;
using System.Net;

using ChatClient.Api.Client.Services;
using ChatClient.Shared.Models;
using ChatClient.Shared.Models.StopAgents;

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

public class MultiAgentSummaryTests
{
    private readonly ITestOutputHelper _output;

    public MultiAgentSummaryTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ThreeAgentsWithReferee_DebateWithSummary_WithDetailedLogging()
    {
        // Arrange
        ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            builder.AddConsole()
                   .AddDebug()
                   .SetMinimumLevel(LogLevel.Debug));

        TestOllamaHandler handler = new(_output);
        HttpClient httpClient = new(handler) { BaseAddress = new Uri("http://localhost") };
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
        AgentDescription refereeDesc = descriptions.First(a => a.AgentName == "Debate Referee / Negotiation Coach");

        _output.WriteLine($"Found agents: Kant, Bentham, Referee (ID: {refereeDesc.AgentId})");

        // Create agents
        _output.WriteLine("Creating Kant agent...");
        ChatCompletionAgent kant = new()
        {
            Name = "Kant",
            Description = "Immanuel Kant",
            Instructions = kantDesc.Content,
            Kernel = kernel,
            HistoryReducer = reducer
        };

        _output.WriteLine("Creating Bentham agent...");
        ChatCompletionAgent bentham = new()
        {
            Name = "Bentham",
            Description = "Jeremy Bentham",
            Instructions = benthamDesc.Content,
            Kernel = kernel,
            HistoryReducer = reducer
        };

        _output.WriteLine("Creating Referee agent...");
        ChatCompletionAgent referee = new()
        {
            Name = refereeDesc.AgentId, // Using AgentId as the Name
            Description = refereeDesc.AgentName,
            Instructions = refereeDesc.Content,
            Kernel = kernel,
            HistoryReducer = reducer
        };

        // Test RoundRobinSummaryGroupChatManager
        _output.WriteLine($"Creating RoundRobinSummaryGroupChatManager with summary agent: {refereeDesc.AgentId}");
        
        var summaryOptions = new RoundRobinSummaryStopAgentOptions
        {
            Rounds = 2,
            SummaryAgent = refereeDesc.AgentId // This should match the agent Name
        };

        RoundRobinSummaryGroupChatManager manager = new(summaryOptions.SummaryAgent)
        {
            MaximumInvocationCount = summaryOptions.Rounds
        };

        _output.WriteLine("Creating group chat orchestration...");
        GroupChatOrchestration chat = new(
            manager,
            kant,
            bentham,
            referee); // Include referee in the team

        _output.WriteLine("Starting runtime...");
        await using InProcessRuntime runtime = new();
        await runtime.StartAsync();

        // Act
        _output.WriteLine("Invoking group chat with question...");
        Microsoft.SemanticKernel.Agents.Orchestration.OrchestrationResult<string> result =
            await chat.InvokeAsync("Is it morally acceptable to lie to save a life?", runtime);

        _output.WriteLine("Waiting for result...");
        string finalResult = await result.GetValueAsync(TimeSpan.FromSeconds(30));

        // Assert
        _output.WriteLine($"Final result received. Handler observed {handler.ObservedRoles.Count} roles");
        _output.WriteLine($"Handler observed messages: {string.Join(", ", handler.ObservedMessages)}");

        // Print all observed interactions
        for (int i = 0; i < handler.ObservedRoles.Count; i++)
        {
            _output.WriteLine($"Message {i + 1}: Role={handler.ObservedRoles[i]}, Content={handler.ObservedMessages[i]}");
        }

        // We expect more messages because of the summary agent
        Assert.True(handler.ObservedRoles.Count >= 2, $"Expected at least 2 messages, got {handler.ObservedRoles.Count}");
        Assert.All(handler.ObservedRoles, r => Assert.Equal("user", r));
    }

    [Fact]
    public void TestRoundRobinSummaryStopAgentOptions_WithRefereeAgent()
    {
        // Arrange
        var options = new RoundRobinSummaryStopAgentOptions
        {
            Rounds = 3,
            SummaryAgent = "𝐀𝐑" // Using the ShortName from the referee agent
        };

        // Act & Assert
        Assert.Equal(3, options.Rounds);
        Assert.Equal("𝐀𝐑", options.SummaryAgent);
        Assert.False(string.IsNullOrEmpty(options.SummaryAgent));
    }

    [Fact]
    public void TestRoundRobinSummaryGroupChatManager_DirectCreation()
    {
        // Arrange
        string summaryAgentName = "𝐀𝐑";
        int rounds = 2;

        // Act
        var manager = new RoundRobinSummaryGroupChatManager(summaryAgentName)
        {
            MaximumInvocationCount = rounds
        };

        // Assert
        Assert.NotNull(manager);
        Assert.Equal(rounds, manager.MaximumInvocationCount);
        
        var requiredAgents = manager.GetRequiredAgents().ToList();
        Assert.Single(requiredAgents);
        Assert.Equal(summaryAgentName, requiredAgents.First());
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

    private sealed class TestOllamaHandler(ITestOutputHelper output) : HttpMessageHandler
    {
        public List<string> ObservedRoles { get; } = [];
        public List<string> ObservedMessages { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            output.WriteLine($"TestOllamaHandler.SendAsync called: {request.Method} {request.RequestUri}");

            if (request.Content == null)
            {
                output.WriteLine("Request content is null");
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            string payload = await request.Content.ReadAsStringAsync(cancellationToken);
            output.WriteLine($"Request payload: {payload}");

            using JsonDocument doc = JsonDocument.Parse(payload);
            JsonElement messages = doc.RootElement.GetProperty("messages");
            List<JsonElement> messagesArray = messages.EnumerateArray().ToList();

            output.WriteLine($"Found {messagesArray.Count} messages in payload");

            if (messagesArray.Count > 0)
            {
                JsonElement last = messagesArray.Last();
                string role = last.GetProperty("role").GetString()!;
                string content = last.GetProperty("content").GetString() ?? "";

                ObservedRoles.Add(role);
                ObservedMessages.Add(content);

                output.WriteLine($"Last message - Role: {role}, Content: {content}");
            }

            // Simulate streaming response
            const string responseJson = "{\"model\":\"phi4\",\"created_at\":\"2024-01-01T00:00:00Z\",\"message\":{\"role\":\"assistant\",\"content\":\"Philosophical response\"},\"done\":true}";

            output.WriteLine($"Returning response: {responseJson}");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}