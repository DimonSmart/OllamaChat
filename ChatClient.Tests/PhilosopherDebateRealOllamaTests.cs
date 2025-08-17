using System.Text.Json;

using ChatClient.Api.Client.Services;
using ChatClient.Api.Services;
using ChatClient.Shared.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0110
#pragma warning disable SKEXP0001

namespace ChatClient.Tests;

public class PhilosopherDebateRealOllamaTests
{
    [Fact]
    public async Task KantAndBentham_DebateLyingToSaveLife()
    {
        ServiceCollection services = new();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        ServiceProvider sp = services.BuildServiceProvider();
        ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        ILogger<PhilosopherDebateRealOllamaTests> testLogger = loggerFactory.CreateLogger<PhilosopherDebateRealOllamaTests>();

        IKernelBuilder builder = Kernel.CreateBuilder();
        HttpClient httpClient = GetHttpClient(loggerFactory);
        builder.AddOllamaChatCompletion(modelId: "qwen3", httpClient: httpClient);
        builder.Services.AddLogging(c => c.AddConsole());
        Kernel kernel = builder.Build();

        List<AgentDescription> descriptions = await LoadDescriptionsAsync();
        AgentDescription kantDesc = descriptions.First(a => a.AgentName == "Immanuel Kant");
        AgentDescription benthamDesc = descriptions.First(a => a.AgentName == "Jeremy Bentham");

        testLogger.LogInformation("Found Kant description: {KantContent}", kantDesc.Content.Take(100));
        testLogger.LogInformation("Found Bentham description: {BenthamContent}", benthamDesc.Content.Take(100));

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

        List<ChatMessageContent> history = [];
        int responseCount = 0;
        int emptyResponseCount = 0;

        GroupChatOrchestration chat = new(
            new RoundRobinGroupChatManager
            {
                MaximumInvocationCount = 2
            },
            kant,
            bentham)
        {
            ResponseCallback = message =>
            {
                responseCount++;
                testLogger.LogInformation("Response #{ResponseCount}: [{AuthorName}] Content: '{Content}' (Length: {Length})",
                    responseCount, message.AuthorName, message.Content?.Take(200), message.Content?.Length ?? 0);

                if (string.IsNullOrWhiteSpace(message.Content))
                {
                    emptyResponseCount++;
                    testLogger.LogWarning("Empty response #{EmptyCount} detected from {AuthorName}! Skipping...", emptyResponseCount, message.AuthorName);
                    return ValueTask.CompletedTask;
                }

                history.Add(message);
                Console.WriteLine($"[{message.AuthorName}] {message.Content}");
                return ValueTask.CompletedTask;
            }
        };

        await using InProcessRuntime runtime = new();
        await runtime.StartAsync();

        testLogger.LogInformation("Starting debate with question: 'Is it morally acceptable to lie to save a life?'");
        Microsoft.SemanticKernel.Agents.Orchestration.OrchestrationResult<string> result = await chat.InvokeAsync("Is it morally acceptable to lie to save a life?", runtime);
        await result.GetValueAsync(TimeSpan.FromMinutes(20));

        testLogger.LogInformation("Debate completed. Total responses: {TotalResponses}, Empty responses: {EmptyResponses}",
            responseCount, emptyResponseCount);
        testLogger.LogInformation("Final history contains {HistoryCount} messages", history.Count);

        Assert.Contains(history, m => m.AuthorName == kant.Name);
        Assert.Contains(history, m => m.AuthorName == bentham.Name);

        Assert.True(history.Count >= 1, $"Expected at least 1 exchange, but got {history.Count}");
        Assert.True(emptyResponseCount == 0, $"SUCCESS! No empty responses: {emptyResponseCount} out of {responseCount}");
    }

    private static async Task<List<AgentDescription>> LoadDescriptionsAsync()
    {
        string currentDir = Directory.GetCurrentDirectory();
        Console.WriteLine($"Current directory: {currentDir}");

        // Try multiple path options
        string[] possiblePaths = new[]
        {
            Path.Combine("ChatClient.Api", "Data", "agent_descriptions.json"),
            Path.Combine("..", "..", "..", "..", "ChatClient.Api", "Data", "agent_descriptions.json"),
            Path.Combine("c:", "Private", "OllamaChat", "ChatClient.Api", "Data", "agent_descriptions.json"),
            Path.GetFullPath(Path.Combine(currentDir, "..", "..", "..", "..", "ChatClient.Api", "Data", "agent_descriptions.json"))
        };

        foreach (string? path in possiblePaths)
        {
            Console.WriteLine($"Trying path: {path}");
            if (File.Exists(path))
            {
                Console.WriteLine($"Found file at: {path}");
                await using FileStream stream = File.OpenRead(path);
                List<AgentDescription>? descriptions = await JsonSerializer.DeserializeAsync<List<AgentDescription>>(stream);
                return descriptions ?? [];
            }
        }

        throw new FileNotFoundException($"Could not find agent_descriptions.json in any of the expected locations. Current dir: {currentDir}");
    }

    private static HttpClient GetHttpClient(ILoggerFactory loggerFactory)
    {
        HttpLoggingHandler logging = new(loggerFactory.CreateLogger<HttpLoggingHandler>())
        {
            InnerHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            }
        };

        return new HttpClient(logging)
        {
            BaseAddress = new Uri("http://localhost:11434"),
            Timeout = TimeSpan.FromMinutes(20)
        };
    }
}
