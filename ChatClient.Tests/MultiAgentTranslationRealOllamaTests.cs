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

public class MultiAgentTranslationRealOllamaTests
{
    [Fact(Skip = "Requires running Ollama server with an English-capable model (e.g. 'llama3.1'). Run manually.")]
    public async Task TwoTranslatorAgents_ChainOfTranslationTranslate()
    {
        var httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:11434") };
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddOllamaChatCompletion(modelId: "qwen3", httpClient: httpClient);
        builder.Services.AddLogging(c => c.AddConsole());
        var kernel = builder.Build();

        var ruToEn = new ChatCompletionAgent
        {
            Name = "ru_to_en",
            Instructions = "Translate the last message from Russian to English. Only reply with the English translation. If the last message isn't Russian, reply with nothing.",
            Kernel = kernel
        };

        var enToEs = new ChatCompletionAgent
        {
            Name = "en_to_es",
            Instructions = "Translate the last message from English to Spanish. Only reply with the Spanish translation. If the last message isn't English, reply with 'OK'.",
            Kernel = kernel
        };

        List<ChatMessageContent> history = [];

        var chatOrchestration = new GroupChatOrchestration(
            new RoundRobinGroupChatManager { MaximumInvocationCount = 2 },
            ruToEn,
            enToEs)
        {
            ResponseCallback = message =>
            {
                history.Add(message);
                return ValueTask.CompletedTask;
            }
        };

        await using var runtime = new InProcessRuntime();
        await runtime.StartAsync();

        var result = await chatOrchestration.InvokeAsync("Привет, как дела?", runtime);
        await result.GetValueAsync(TimeSpan.FromSeconds(30));

        foreach (var message in history)
        {
            Console.WriteLine($"{message.Role} ({message.AuthorName}): {message.Content}");
        }
    }
}
