using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
#pragma warning disable SKEXP0110

namespace ChatClient.Tests;

public class MultiAgentTranslationRealOllamaTests
{
    [Fact(Skip = "Requires running Ollama server with an English-capable model (e.g. 'llama3.1'). Run manually.")]
    public async Task TwoTranslatorAgents_TranslateRussianToFrench()
    {
        var httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:11434") };
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddOllamaChatCompletion(modelId: "llama3.1", httpClient: httpClient);
        builder.Services.AddLogging(c => c.AddConsole());
        var kernel = builder.Build();

        var ruToEn = new ChatCompletionAgent
        {
            Name = "ru_to_en",
            Instructions = "Translate the last message from Russian to English. Only reply with the English translation. If the last message isn't Russian, reply with nothing.",
            Kernel = kernel
        };

        var enToFr = new ChatCompletionAgent
        {
            Name = "en_to_fr",
            Instructions = "Translate the last message from English to French. Only reply with the French translation. If the last message isn't English, reply with nothing.",
            Kernel = kernel
        };

        var chat = new AgentGroupChat();
        chat.AddChatMessage(new ChatMessageContent(AuthorRole.User, "Привет, как дела?"));

        string ruEnTranslation = string.Empty;
        await foreach (var content in chat.InvokeAsync(ruToEn, CancellationToken.None))
        {
            if (!string.IsNullOrWhiteSpace(content.Content))
            {
                ruEnTranslation += content.Content;
            }
        }
        Assert.Contains("Hello", ruEnTranslation, StringComparison.OrdinalIgnoreCase);

        string enFrTranslation = string.Empty;
        await foreach (var content in chat.InvokeAsync(enToFr, CancellationToken.None))
        {
            if (!string.IsNullOrWhiteSpace(content.Content))
            {
                enFrTranslation += content.Content;
            }
        }
        Assert.Contains("Bonjour", enFrTranslation, StringComparison.OrdinalIgnoreCase);
    }
}
