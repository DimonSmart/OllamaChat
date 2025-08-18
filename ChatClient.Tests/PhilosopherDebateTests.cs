using System.Text.Json;

using ChatClient.Api.Client.Services;
using ChatClient.Api.Services;
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
        var service = new SequentialResponseChatCompletionService();
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

        Assert.Equal(2, service.ObservedRoles.Count);
        Assert.All(service.ObservedRoles, r => Assert.Equal(AuthorRole.User, r));
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

    private sealed class SequentialResponseChatCompletionService : IChatCompletionService
    {
        private int _counter;
        public List<AuthorRole> ObservedRoles { get; } = [];
        private readonly ForceLastUserReducer _reducer = new();
        public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            var reduced = _reducer.ReduceAsync(chatHistory, cancellationToken).Result ?? chatHistory;
            ObservedRoles.Add(reduced.Last().Role);
            _counter++;
            ChatMessageContent message = new(AuthorRole.Assistant, _counter.ToString(), "assistant");
            return Task.FromResult<IReadOnlyList<ChatMessageContent>>([message]);
        }

        public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            var reduced = _reducer.ReduceAsync(chatHistory, cancellationToken).Result ?? chatHistory;
            ObservedRoles.Add(reduced.Last().Role);
            _counter++;
            StreamingChatMessageContent content = new(AuthorRole.Assistant, _counter.ToString())
            {
                AuthorName = "assistant"
            };

            return Stream(content);
        }

        private static async IAsyncEnumerable<StreamingChatMessageContent> Stream(StreamingChatMessageContent content)
        {
            yield return content;
            await Task.CompletedTask;
        }
    }
}
