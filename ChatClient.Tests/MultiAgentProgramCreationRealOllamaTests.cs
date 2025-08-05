using ChatClient.Api.Services;

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

public class MultiAgentTests
{
    [Fact(Skip = "Requires running Ollama server with 'phi4:latest' model.")]
    public async Task CopyWriterReviewer_CreateSlogan()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        var serviceProvider = services.BuildServiceProvider();
        ILoggerFactory loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

        IKernelBuilder builder = Kernel.CreateBuilder();
        var httpClient = GetHttpClient(loggerFactory);
        builder.AddOllamaChatCompletion(modelId: "phi4", httpClient: httpClient);
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
                The goal is to determine if the last assistant text is acceptable to print?
                If yes, answer with: 'I Approve'
                If not, answer with: 'Not approved' and provide insight as a 'Comment:' on how to refine text without example.
                """,
            Kernel = kernel,
        };

        List<ChatMessageContent> history = [];

        var chatOrchestration = new GroupChatOrchestration(
            new ReasonableRoundRobinGroupChatManager(reviewer.Name, "I Approve", "The reviewer has approved the copy.")
            {
                MaximumInvocationCount = 5
            },
            writer,
            reviewer)
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

        var result = await chatOrchestration.InvokeAsync(
            "Create a slogan for a new electric SUV that is affordable and fun to drive.",
            runtime);
        await result.GetValueAsync(TimeSpan.FromMinutes(20));

        Assert.NotEmpty(history);
        Assert.Contains(history, message => message.Content?.Contains("I Approve", StringComparison.OrdinalIgnoreCase) == true);
    }

    private sealed class ReasonableRoundRobinGroupChatManager(string reviewerName, string stopPhraze, string reason) : RoundRobinGroupChatManager
    {
        public override async ValueTask<GroupChatManagerResult<bool>> ShouldTerminate(
            ChatHistory history, CancellationToken cancellationToken = default)
        {
            var baseResult = await base.ShouldTerminate(history, cancellationToken);
            if (baseResult.Value)
                return baseResult;

            var lastByReviewer = history.LastOrDefault(m => m.AuthorName == reviewerName);
            if (lastByReviewer?.ToString()?.Contains(stopPhraze, StringComparison.OrdinalIgnoreCase) == true)
                return new(true) { Reason = reason };

            return baseResult;
        }
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

