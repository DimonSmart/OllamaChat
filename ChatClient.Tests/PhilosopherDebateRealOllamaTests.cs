using System.Text.Json;

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
    [Fact(Skip = "Requires running Ollama server with 'phi4:latest' model.")]
    public async Task KantAndBentham_DebateLyingToSaveLife()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        var sp = services.BuildServiceProvider();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

        IKernelBuilder builder = Kernel.CreateBuilder();
        var httpClient = GetHttpClient(loggerFactory);
        builder.AddOllamaChatCompletion(modelId: "phi4", httpClient: httpClient);
        builder.Services.AddLogging(c => c.AddConsole());
        var kernel = builder.Build();

        var descriptions = await LoadDescriptionsAsync();
        var kantDesc = descriptions.First(a => a.AgentName == "Immanuel Kant");
        var benthamDesc = descriptions.First(a => a.AgentName == "Jeremy Bentham");

        var kant = new ChatCompletionAgent
        {
            Name = "Kant",
            Description = "Immanuel Kant",
            Instructions = kantDesc.Content,
            Kernel = kernel
        };

        var bentham = new ChatCompletionAgent
        {
            Name = "Bentham",
            Description = "Jeremy Bentham",
            Instructions = benthamDesc.Content,
            Kernel = kernel
        };

        List<ChatMessageContent> history = [];
        var chat = new GroupChatOrchestration(
            new RoundRobinGroupChatManager
            {
                MaximumInvocationCount = 6
            },
            kant,
            bentham)
        {
            ResponseCallback = message =>
            {
                history.Add(message);
                Console.WriteLine($"[{message.AuthorName}] {message.Content}");
                return ValueTask.CompletedTask;
            }
        };

        await using var runtime = new InProcessRuntime();
        await runtime.StartAsync();

        var result = await chat.InvokeAsync("Можно ли врать ради спасения жизни?", runtime);
        await result.GetValueAsync(TimeSpan.FromMinutes(20));

        Assert.Contains(history, m => m.AuthorName == kant.Name);
        Assert.Contains(history, m => m.AuthorName == bentham.Name);
    }

    private static async Task<List<AgentDescription>> LoadDescriptionsAsync()
    {
        var path = Path.Combine("ChatClient.Api", "Data", "agent_descriptions.json");
        await using var stream = File.OpenRead(path);
        var descriptions = await JsonSerializer.DeserializeAsync<List<AgentDescription>>(stream);
        return descriptions ?? [];
    }

    private static HttpClient GetHttpClient(ILoggerFactory loggerFactory)
    {
        var logging = new HttpLoggingHandler(loggerFactory.CreateLogger<HttpLoggingHandler>())
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
