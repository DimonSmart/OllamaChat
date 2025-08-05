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

public class MultiAgentProgramCreationRealOllamaTests
{
    [Fact(Skip = "Requires running Ollama server with 'phi4:latest' model.")]
    public async Task ProgramManagerSoftwareEngineerProjectManager_CreateCalculatorApp()
    {
        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddOllamaChatCompletion(modelId: "phi4:latest");
        builder.Services.AddLogging(c => c.AddConsole());
        builder.Services.AddSingleton<IPromptRenderFilter>(new ContextInjectorFilter(HistoryMode.LastMessage));
        var kernel = builder.Build();

        var programManager = new ChatCompletionAgent
        {
            Name = "program_manager",
            Description = "Program manager",
            Instructions = """
You are a program manager.
Here is the previous context:
{{$promptContext}}

Take the user's requirement and create a plan.
""",
            Kernel = kernel
        };

        var softwareEngineer = new ChatCompletionAgent
        {
            Name = "software_engineer",
            Description = "Software engineer",
            Instructions = """
You are a software engineer.
Here is the previous context:
{{$promptContext}}

Create the HTML and JavaScript code that satisfies the plan.
""",
            Kernel = kernel
        };

        var projectManager = new ChatCompletionAgent
        {
            Name = "project_manager",
            Description = "Project manager",
            Instructions = """
You are a project manager.
Here is the previous context:
{{$promptContext}}

Review the work and respond with 'approve' when the requirements are met.
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
}

