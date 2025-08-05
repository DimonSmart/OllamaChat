using System.Net.Http.Headers;
using System.Text;

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

public class MultiAgentProgramCreationRealOllamaTests
{
    [Fact()] //Skip = "Requires running Ollama server with 'phi4:latest' model.")]
    public async Task ProgramManagerSoftwareEngineerProjectManager_CreateCalculatorApp()
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

        var programManager = new ChatCompletionAgent
        {
            Name = "program_manager",
            Description = "Program manager",
            Instructions = """
You are a program manager which will take the requirement and create a plan for creating app. Program Manager understands the user requirements and form the detail documents with requirements and costing.
""",
            Kernel = kernel
        };

        var softwareEngineer = new ChatCompletionAgent
        {
            Name = "software_engineer",
            Description = "Software engineer",
            Instructions = """
You are Software Engieer, and your goal is create web app using HTML and JavaScript by taking into consideration all the requirements given by Program Manager.
""",
            Kernel = kernel
        };

        var projectManager = new ChatCompletionAgent
        {
            Name = "project_manager",
            Description = "Project manager",
            Instructions = """
You are manager which will review software engineer code, and make sure all client requirements are completed.
You are the guardian of quality, ensuring the final product meets all specifications and receives the green light for release.
Once all client requirements are completed, you can approve the request by just responding "approve"
""",
            Kernel = kernel
        };

        List<ChatMessageContent> history = [];

        var chatOrchestration = new GroupChatOrchestration(
            new RoundRobinGroupChatManager { MaximumInvocationCount = 6 },
            programManager,
            softwareEngineer,
            projectManager)
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
            "I want to develop app which will provide me calculator. Keep it very simple. And get final approval from manager.",
            runtime);
        await result.GetValueAsync(TimeSpan.FromMinutes(20));

        Assert.NotEmpty(history);
        Assert.Contains("approve", history.Last().Content!, StringComparison.OrdinalIgnoreCase);
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

