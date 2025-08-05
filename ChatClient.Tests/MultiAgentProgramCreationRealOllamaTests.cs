using System.Net.Http.Headers;
using System.Text;

using ChatClient.Api.Services;
using ChatClient.Shared.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;

#pragma warning disable SKEXP0110
#pragma warning disable SKEXP0001

namespace ChatClient.Tests;

public class MultiAgentProgramCreationRealOllamaTests
{
    [Fact] //Skip = "Requires running Ollama server with 'phi4:latest' model.")]
    public async Task CopyWriterReviewer_CreateSlogan()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        var serviceProvider = services.BuildServiceProvider();
        ILoggerFactory loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

        IKernelBuilder builder = Kernel.CreateBuilder();
        var httpClient = GetHttpClient(loggerFactory);
        builder.AddOllamaChatCompletion(modelId: "mistral-small3.2:latest", httpClient: httpClient); //"phi4:latest" "gemma3"
        builder.Services.AddLogging(c => c.AddConsole());
        var kernel = builder.Build();

        var writer = new ChatCompletionAgent
        {
            Name = "copy_writer",
            Description = "A copy writer",
            Instructions = """
You are a copywriter with ten years of experience and are known for brevity and a dry humor.
The goal is to refine and decide on the single best copy as an expert in the field.
Only provide a single proposal per response.
You're laser focused on the goal at hand.
Don't waste time with chit chat.
Consider suggestions when refining an idea.
""",
            Kernel = kernel
        };

        var reviewer = new ChatCompletionAgent
        {
            Name = "reviewer",
            Description = "An editor",
            Instructions = """
You are an art director who has opinions about copywriting born of a love for David Ogilvy.
The goal is to determine if the given copy is acceptable to print.
If so, state: "I Approve".
If not, provide insight on how to refine suggested copy without example.
""",
            Kernel = kernel
        };

        List<ChatMessageContent> history = [];

        var chatOrchestration = new GroupChatOrchestration(
            new AuthorCriticManager(writer.Name!, reviewer.Name!)
            {
                MaximumInvocationCount = 5
            },
            writer,
            reviewer)
        {
            ResponseCallback = message =>
            {
                if (message.Role == AuthorRole.Assistant)
                {
                    history.Add(message);
                }
                return ValueTask.CompletedTask;
            }
        };

        await using var runtime = new InProcessRuntime();
        await runtime.StartAsync();

        var result = await chatOrchestration.InvokeAsync(
            "Create a slogan for a new electric SUV that is affordable and fun to drive.",
            runtime);
        await result.GetValueAsync(TimeSpan.FromMinutes(20));

        Assert.NotEmpty(history);
        Assert.Contains("I Approve", history.Last().Content!, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class AuthorCriticManager(string authorName, string criticName) : RoundRobinGroupChatManager
    {
        public override ValueTask<GroupChatManagerResult<string>> FilterResults(ChatHistory history, CancellationToken cancellationToken = default)
        {
            ChatMessageContent finalResult = history.Last(message => message.AuthorName == authorName);
            return ValueTask.FromResult(new GroupChatManagerResult<string>($"{finalResult}") { Reason = "The approved copy." });
        }

        public override async ValueTask<GroupChatManagerResult<bool>> ShouldTerminate(ChatHistory history, CancellationToken cancellationToken = default)
        {
            GroupChatManagerResult<bool> result = await base.ShouldTerminate(history, cancellationToken);
            if (!result.Value)
            {
                ChatMessageContent? lastMessage = history.LastOrDefault();
                if (lastMessage is not null && lastMessage.AuthorName == criticName && $"{lastMessage}".Contains("I Approve", StringComparison.OrdinalIgnoreCase))
                {
                    result = new GroupChatManagerResult<bool>(true) { Reason = "The reviewer has approved the copy." };
                }
            }
            return result;
        }
    }

    private static HttpClient GetHttpClient(ILoggerFactory loggerFactory)
    {
        var handler = new HttpClientHandler();

        handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
        var loggingHandler = new HttpLoggingHandler(loggerFactory.CreateLogger<HttpLoggingHandler>())
        {
            InnerHandler = handler
        };

        var httpClient = new HttpClient(loggingHandler)
        {
            BaseAddress = new Uri("https://92.127.231.222:8043"),
            Timeout = TimeSpan.FromMinutes(100)
        };

        var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($":Codex"));
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authValue);

        return httpClient;
    }
}

