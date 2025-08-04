using ChatClient.Api.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
#pragma warning disable SKEXP0110
#pragma warning disable SKEXP0001

namespace ChatClient.Tests;

public class MultiAgentTranslationRealOllamaTests
{
    [Fact(Timeout = 240000)]
    public async Task TwoTranslatorAgents_ChainOfTranslationTranslate()
    {
        // Set up logging for HttpClient
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        var serviceProvider = services.BuildServiceProvider();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

        var handler = new HttpClientHandler();
        var loggingHandler = new HttpLoggingHandler(loggerFactory.CreateLogger<HttpLoggingHandler>())
        {
            InnerHandler = handler
        };

        var httpClient = new HttpClient(loggingHandler)
        {
            BaseAddress = new Uri("http://localhost:11434"),
            Timeout = TimeSpan.FromMinutes(100)
        };

        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddOllamaChatCompletion(modelId: "phi4:latest", httpClient: httpClient);
        builder.Services.AddLogging(c => c.AddConsole());
        var kernel = builder.Build();

        var recipe_creator = new ChatCompletionAgent
        {
            Name = "recipe_creator",
            Description = "Recipe creator",
            Instructions = """
            You are professional Chef.
            Create simple recepy based on initial user input            
            """,
            Kernel = kernel
        };

        var ingredients_extractor = new ChatCompletionAgent
        {
            Name = "shopping_list_creater",
            Description = "Shopping list creator",
            Instructions = """
You are a chef assistant. You will be given a recipe in markdown form.
Extract the ingredients and output a numbered list, one ingredient per line, preserving quantity/optionality if present.
Example input:
**Simple Scrambled Eggs with Herbs**
### Ingredients:
- 4 large eggs
- Salt, to taste
- Black pepper, to taste
- 1 tablespoon milk or cream (optional)
- 2 tablespoons butter
- Fresh herbs (such as chives, parsley, and/or dill), chopped

Example output:
1. 4 large eggs
2. Salt, to taste
3. Black pepper, to taste
4. 1 tablespoon milk or cream (optional)
5. 2 tablespoons butter
6. Fresh herbs (chives/parsley/dill), chopped

If you cannot extract ingredients, explain why in one sentence.
""",
            Kernel = kernel
        };

        var upperCaser = new ChatCompletionAgent
        {
            Name = "upper_case_maker",
            Description = "Make message uppercase",
            Instructions = "You are a formatter. Take all previous messages in order, join them into one long string, make it UPPERCASE, and output only that. If there are no previous messages, output exactly: NO CONTENT. Do not add anything else. If the text is too long, keep as much of the most recent part as fits.",
            Kernel = kernel
        };

        List<ChatMessageContent> history = [];

        var chatOrchestration = new GroupChatOrchestration(
            new RoundRobinGroupChatManager { MaximumInvocationCount = 3 },
             recipe_creator,
            ingredients_extractor,
            upperCaser)
        {
            ResponseCallback = message =>
            {
                Console.WriteLine($"Agent response: {message.Role.Label ?? "unknown"} / {message.Content}");
                history.Add(message);
                return ValueTask.CompletedTask;
            }
        };

        await using var runtime = new InProcessRuntime();
        await runtime.StartAsync();

        var result = await chatOrchestration.InvokeAsync("Eggs for breakfast", runtime);
        await result.GetValueAsync(TimeSpan.FromMinutes(20));

        Assert.Equal(6, history.Count);
        Assert.Contains("Egg", history[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Egg", history[1].Content, StringComparison.OrdinalIgnoreCase);
    }
}
