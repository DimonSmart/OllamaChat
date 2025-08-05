using ChatClient.Api.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Orchestration.GroupChat;
using Microsoft.SemanticKernel.Agents.Runtime.InProcess;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Linq;
#pragma warning disable SKEXP0110
#pragma warning disable SKEXP0001

namespace ChatClient.Tests;

public enum HistoryMode
{
    FullHistory,
    LastMessage
}

public class ContextInjectorFilter(HistoryMode mode) : IPromptRenderFilter
{
    private readonly HistoryMode _mode = mode;

    public async Task OnPromptRenderAsync(
        PromptRenderContext context,
        Func<PromptRenderContext, Task> next)
    {
        context.Arguments.TryGetValue("history", out object? rawHistory);

        string contextText = string.Empty;

        if (rawHistory is IReadOnlyList<ChatMessageContent> list && list.Count > 0)
        {
            if (_mode == HistoryMode.FullHistory)
            {
                contextText = string.Join("\n", list.Select(m => $"[{m.Role.Label}] {m.Content?.Trim()}"));
            }
            else
            {
                var last = list.Last();
                contextText = $"[{last.Role.Label}] {last.Content?.Trim()}";
            }
        }

        context.Arguments["promptContext"] = contextText;

        await next(context);

        if (context is not null)
        {
            // Fallback substitution in case the prompt renderer didn't replace placeholders
            // Use both {{promptContext}} and {{$promptContext}} syntaxes
            var rendered = context.RenderedPrompt;
            if (!string.IsNullOrEmpty(rendered))
            {
                rendered = rendered.Replace("{{promptContext}}", contextText)
                                     .Replace("{{$promptContext}}", contextText);
                context.RenderedPrompt = rendered;
            }
        }
    }
}

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
        builder.Services.AddSingleton<IPromptRenderFilter>(new ContextInjectorFilter(HistoryMode.LastMessage));
        var kernel = builder.Build();

        var recipe_creator = new ChatCompletionAgent
        {
            Name = "recipe_creator",
            Description = "Recipe creator",
            Instructions = """
You are a professional chef.
Here is the previous context:
{{$promptContext}}

Create a simple recipe for: {{$input}}
""",
            Kernel = kernel
        };

        var ingredients_extractor = new ChatCompletionAgent
        {
            Name = "shopping_list_creater",
            Description = "Shopping list creator",
            Instructions = """
You are a chef assistant.
Here is the previous message:
{{$promptContext}}

Extract the ingredients and output a numbered list, one ingredient per line, preserving quantity/optionality if present.
If you cannot extract ingredients, explain why in one sentence.
""",
            Kernel = kernel
        };

        var upperCaser = new ChatCompletionAgent
        {
            Name = "upper_case_maker",
            Description = "Make message uppercase",
            Instructions = """
You are a formatter.
Here is the previous message:
{{$promptContext}}

Return it in uppercase, preserving line breaks.
""",
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
                if (message.Role == AuthorRole.Assistant)
                {
                    history.Add(message);
                }
                return ValueTask.CompletedTask;
            }
        };

        await using var runtime = new InProcessRuntime();
        await runtime.StartAsync();

        var result = await chatOrchestration.InvokeAsync("Eggs for breakfast", runtime);
        await result.GetValueAsync(TimeSpan.FromMinutes(20));

        Assert.Equal(3, history.Count);
        Assert.Contains("Egg", history[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(history[1].Content);
        Assert.Contains("1", history[1].Content);
        Assert.Equal(history[1].Content!.ToUpperInvariant().Trim(), history[2].Content!.Trim());
    }
}
