using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
#pragma warning disable SKEXP0110

namespace ChatClient.Tests;

public class MultiAgentTranslationRealOllamaTests
{
    [Fact()] //Skip = "Requires running Ollama server with an English-capable model (e.g. 'llama3.1'). Run manually.")]
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

        // Теперь агенты явно добавлены в чат:
        var chat = new AgentGroupChat(ruToEn, enToEs);
        chat.AddChatMessage(new ChatMessageContent(AuthorRole.User, "Привет, как дела?"));

        // Вручную ограничиваем количество ходов:
        int turns = 0;
        int maxTurns = 2;

        await foreach (var message in chat.InvokeAsync(CancellationToken.None))
        {
#pragma warning disable SKEXP0001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            Console.WriteLine($"{message.Role} ({message.AuthorName}): {message.Content}");
#pragma warning restore SKEXP0001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

            turns++;
            if (turns >= maxTurns)
                break;
        }


    }
}
