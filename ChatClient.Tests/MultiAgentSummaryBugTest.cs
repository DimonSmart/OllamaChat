using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

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

public class MultiAgentSummaryBugTest
{
    private readonly ITestOutputHelper _output;

    public MultiAgentSummaryBugTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ReproduceBugWithEmptySummaryAgent_ShouldShowError()
    {
        // This test reproduces the bug when SummaryAgent is empty or wrong
        List<AgentDescription> descriptions = await LoadDescriptionsAsync();
        AgentDescription kantDesc = descriptions.First(a => a.AgentName == "Immanuel Kant");
        AgentDescription benthamDesc = descriptions.First(a => a.AgentName == "Jeremy Bentham");

        // Create a mock list of agents like in MultiAgentChat.razor
        var agents = new List<AgentDescription> { kantDesc, benthamDesc };

        // Simulate creating RoundRobinSummaryStopAgentOptions with empty SummaryAgent
        var summaryOptions = new RoundRobinSummaryStopAgentOptions
        {
            Rounds = 2,
            SummaryAgent = string.Empty  // This is the bug - empty summary agent
        };

        _output.WriteLine($"Summary agent (empty): '{summaryOptions.SummaryAgent}'");

        // Simulate the logic from MultiAgentChat.razor StartChat method
        var agentsForChat = new List<AgentDescription> { kantDesc, benthamDesc };
        var manager = new RoundRobinSummaryGroupChatManager(summaryOptions.SummaryAgent);

        if (manager is IGroupChatAgentProvider provider)
        {
            foreach (var agentId in provider.GetRequiredAgents())
            {
                _output.WriteLine($"Looking for required agent: '{agentId}'");
                var agent = agents.FirstOrDefault(a => a.AgentId == agentId);
                if (agent is null)
                {
                    _output.WriteLine($"❌ Required agent '{agentId}' not found in available agents!");
                    _output.WriteLine($"Available agents: {string.Join(", ", agents.Select(a => $"'{a.AgentId}'"))}");
                }
                else
                {
                    _output.WriteLine($"✅ Found required agent: '{agent.AgentId}' ({agent.AgentName})");
                    if (agentsForChat.All(a => a.AgentId != agent.AgentId))
                        agentsForChat.Add(agent);
                }
            }
        }

        _output.WriteLine($"Final agents for chat: {agentsForChat.Count}");
        Assert.True(agentsForChat.Count >= 2, "Should have at least the original agents");

        // If we got here without exception, the bug might be elsewhere
        // But the main issue is when SummaryAgent is not found in the agents list
    }

    [Fact]
    public async Task ReproduceBugWithWrongSummaryAgent_ShouldShowError()
    {
        // This test reproduces the bug when SummaryAgent has wrong value
        List<AgentDescription> descriptions = await LoadDescriptionsAsync();
        AgentDescription kantDesc = descriptions.First(a => a.AgentName == "Immanuel Kant");
        AgentDescription benthamDesc = descriptions.First(a => a.AgentName == "Jeremy Bentham");

        // Create a mock list of agents like in MultiAgentChat.razor (WITHOUT referee)
        var agents = new List<AgentDescription> { kantDesc, benthamDesc };

        // Simulate creating RoundRobinSummaryStopAgentOptions with wrong SummaryAgent
        var summaryOptions = new RoundRobinSummaryStopAgentOptions
        {
            Rounds = 2,
            SummaryAgent = "𝐀𝐑"  // This agent is not in the agents list - should cause error
        };

        _output.WriteLine($"Summary agent (wrong): '{summaryOptions.SummaryAgent}'");
        _output.WriteLine($"Available agents: {string.Join(", ", agents.Select(a => $"'{a.AgentId}' ({a.AgentName})"))}");

        // Simulate the logic from MultiAgentChat.razor StartChat method
        var agentsForChat = new List<AgentDescription> { kantDesc, benthamDesc };
        var manager = new RoundRobinSummaryGroupChatManager(summaryOptions.SummaryAgent);

        bool foundMissingAgent = false;
        if (manager is IGroupChatAgentProvider provider)
        {
            foreach (var agentId in provider.GetRequiredAgents())
            {
                _output.WriteLine($"Looking for required agent: '{agentId}'");
                var agent = agents.FirstOrDefault(a => a.AgentId == agentId);
                if (agent is null)
                {
                    foundMissingAgent = true;
                    _output.WriteLine($"❌ Required agent '{agentId}' not found in available agents!");
                    _output.WriteLine("This is the bug - required agent is missing from selected agents!");
                }
                else
                {
                    _output.WriteLine($"✅ Found required agent: '{agent.AgentId}' ({agent.AgentName})");
                    if (agentsForChat.All(a => a.AgentId != agent.AgentId))
                        agentsForChat.Add(agent);
                }
            }
        }

        // This should demonstrate the bug
        Assert.True(foundMissingAgent, "Should have found the missing summary agent - this is the bug!");
        _output.WriteLine("✅ Successfully reproduced the missing agent bug!");
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

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            _output.WriteLine($"GetChatMessageContentsAsync called with {chatHistory.Count()} messages");
            return Task.FromResult<IReadOnlyList<ChatMessageContent>>(new[] { new ChatMessageContent(AuthorRole.Assistant, "Philosophical response from mock service", "assistant") });
        }

        public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _output.WriteLine($"GetStreamingChatMessageContentsAsync called with {chatHistory.Count()} messages");

            // Simulate streaming response
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, "Philosophical", "assistant");
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, " response", "assistant");
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
