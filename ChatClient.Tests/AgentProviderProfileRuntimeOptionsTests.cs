using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable MAAI001

namespace ChatClient.Tests;

public class AgentProviderProfileRuntimeOptionsTests
{
    [Fact]
    public void BuildHarnessAgentOptions_AppliesResolvedCompactionWithoutReplacingStrategy()
    {
        var strategy = new ContextWindowCompactionStrategy(16_000, 2_000, 0.5, 0.8);
        var resolved = new ResolvedCompactionStrategy(
            strategy,
            new CompactionBudget(16_000, 2_000, 14_000),
            []);

        var options = BuildHarnessAgentOptions(resolved);

        Assert.False(options.DisableCompaction);
        Assert.Same(strategy, options.CompactionStrategy);
        Assert.Equal(16_000, options.MaxContextWindowTokens);
        Assert.Equal(2_000, options.MaxOutputTokens);
    }

    [Fact]
    public void BuildHarnessAgentOptions_DisablesCompactionWhenNoProfileIsResolved()
    {
        var options = BuildHarnessAgentOptions(null);

        Assert.True(options.DisableCompaction);
        Assert.Null(options.CompactionStrategy);
        Assert.Null(options.MaxContextWindowTokens);
        Assert.Null(options.MaxOutputTokens);
    }

    [Fact]
    public void BuildTodoProviderOptions_MapsProfileAndRendersTemplate()
    {
        var options = AgenticRuntimeAgentFactory.BuildTodoProviderOptions(new TodoProviderProfile
        {
            Instructions = " Use a concise plan. ",
            SuppressTodoListMessage = true,
            TodoListMessageTemplate = "Current work:\n{todos}"
        });

        Assert.Equal("Use a concise plan.", options.Instructions);
        Assert.True(options.SuppressTodoListMessage);
        var message = options.TodoListMessageBuilder!([
            new TodoItem { Title = "Plan", Description = "Outline the work", IsComplete = true },
            new TodoItem { Title = "Implement" }
        ]);
        Assert.Equal($"Current work:\n- [x] Plan: Outline the work{Environment.NewLine}- [ ] Implement", message);
    }

    [Fact]
    public void BuildAgentModeProviderOptions_MapsInstructionsModesAndDefault()
    {
        var options = AgenticRuntimeAgentFactory.BuildAgentModeProviderOptions(new AgentModeProviderProfile
        {
            Instructions = "Modes: {available_modes}; current: {current_mode}",
            DefaultMode = "execute",
            Modes =
            [
                new AgentModeProfile { Name = "plan", Instructions = "Plan with the user." },
                new AgentModeProfile { Name = "execute", Instructions = "Perform approved work." }
            ]
        });

        Assert.Equal("Modes: {available_modes}; current: {current_mode}", options.Instructions);
        Assert.Equal("execute", options.DefaultMode);
        Assert.Collection(
            options.Modes!,
            mode => Assert.Equal(("plan", "Plan with the user."), (mode.Name, mode.Instructions)),
            mode => Assert.Equal(("execute", "Perform approved work."), (mode.Name, mode.Instructions)));
    }

    [Theory]
    [InlineData(FileAccessMode.ReadOnly, true)]
    [InlineData(FileAccessMode.ReadWrite, false)]
    public void BuildFileAccessProviderOptions_MapsPositivePolicy(FileAccessMode accessMode, bool disableWriteTools)
    {
        var options = AgenticRuntimeAgentFactory.BuildFileAccessProviderOptions(new FileAccessProviderProfile
        {
            Instructions = " Use relative paths. ",
            AccessMode = accessMode,
            RequireReadApproval = true,
            RequireWriteApproval = false
        });

        Assert.Equal("Use relative paths.", options.Instructions);
        Assert.Equal(disableWriteTools, options.DisableWriteTools);
        Assert.False(options.DisableReadOnlyToolApproval);
        Assert.True(options.DisableWriteToolApproval);
    }

    [Fact]
    public void BuildContextProviders_AddsConfiguredTodoProviderWithoutDefaultProvider()
    {
        var request = new AgentRunRequest
        {
            Agent = new AgentExecutionSpec { Id = Guid.NewGuid() },
            ResolvedModel = new ServerModel(Guid.NewGuid(), "test-model"),
            Configuration = new AppChatConfiguration("test-model", []),
            Conversation = [],
            UserMessage = "Hello"
        };

        var providers = AgenticRuntimeAgentFactory.BuildContextProviders(
            request,
            null!,
            hasRagContent: false,
            supportsFunctions: false,
            loggerFactory: NullLoggerFactory.Instance,
            todoProfile: new TodoProviderProfile { Instructions = "Track the work." });

        Assert.Single(providers);
        Assert.IsType<TodoProvider>(providers[0]);
    }

    [Fact]
    public void AgentExecutionSpecFactory_PreservesProviderSelections()
    {
        var todoProfileId = Guid.NewGuid();
        var modeProfileId = Guid.NewGuid();
        var fileAccessProfileId = Guid.NewGuid();
        var spec = AgentExecutionSpecFactory.FromTemplate(new AgentTemplateDefinition
        {
            TodoProviderProfileId = todoProfileId,
            AgentModeProviderProfileId = modeProfileId,
            FileAccessProviderProfileId = fileAccessProfileId
        });

        Assert.Equal(todoProfileId, spec.TodoProviderProfileId);
        Assert.Equal(modeProfileId, spec.AgentModeProviderProfileId);
        Assert.Equal(fileAccessProfileId, spec.FileAccessProviderProfileId);
    }

    [Fact]
    public void AgentTemplateClone_PreservesProviderSelections()
    {
        var todoProfileId = Guid.NewGuid();
        var modeProfileId = Guid.NewGuid();
        var fileAccessProfileId = Guid.NewGuid();

        var clone = new AgentTemplateDefinition
        {
            TodoProviderProfileId = todoProfileId,
            AgentModeProviderProfileId = modeProfileId,
            FileAccessProviderProfileId = fileAccessProfileId
        }.Clone();

        Assert.Equal(todoProfileId, clone.TodoProviderProfileId);
        Assert.Equal(modeProfileId, clone.AgentModeProviderProfileId);
        Assert.Equal(fileAccessProfileId, clone.FileAccessProviderProfileId);
    }

    [Fact]
    public void TodoCompletionLoop_IsNotConfiguredWhenDisabled()
    {
        var options = new HarnessAgentOptions();

        AgenticRuntimeAgentFactory.ConfigureTodoCompletionLoop(
            options,
            new AgentExecutionSpec { ContinueUntilTodosComplete = false });

        Assert.Null(options.LoopEvaluators);
        Assert.Null(options.LoopAgentOptions);
    }

    [Fact]
    public void TodoCompletionLoop_UsesExecuteOnlyAndConfiguredMaximum()
    {
        var options = new HarnessAgentOptions();

        AgenticRuntimeAgentFactory.ConfigureTodoCompletionLoop(
            options,
            new AgentExecutionSpec
            {
                ContinueUntilTodosComplete = true,
                MaxTodoCompletionIterations = 7
            });

        var evaluator = Assert.IsType<TodoCompletionLoopEvaluator>(Assert.Single(options.LoopEvaluators!));
        Assert.NotNull(evaluator);
        Assert.Equal(7, options.LoopAgentOptions!.MaxIterations);
        Assert.True(options.LoopAgentOptions.ExcludeOnBehalfOfMessages);
    }

    [Fact]
    public void TodoCompletionLoop_RejectsMissingProvidersAndNonOrdinalExecuteMode()
    {
        var agent = new AgentExecutionSpec { ContinueUntilTodosComplete = true };

        var missingTodo = Assert.Throws<InvalidOperationException>(() =>
            AgenticRuntimeAgentFactory.ValidateTodoCompletionConfiguration(agent, null, new AgentModeProviderProfile
            {
                Modes = [new AgentModeProfile { Name = "execute", Instructions = "run" }]
            }));
        Assert.Contains("Todo provider", missingTodo.Message);

        var missingExecute = Assert.Throws<InvalidOperationException>(() =>
            AgenticRuntimeAgentFactory.ValidateTodoCompletionConfiguration(agent, new TodoProviderProfile(), new AgentModeProviderProfile
            {
                Modes = [new AgentModeProfile { Name = "Execute", Instructions = "run" }]
            }));
        Assert.Contains("execute", missingExecute.Message);
    }

    [Fact]
    public void AgentExecutionSpecFactory_PreservesTodoCompletionSettings()
    {
        var spec = AgentExecutionSpecFactory.FromTemplate(new AgentTemplateDefinition
        {
            ContinueUntilTodosComplete = true,
            MaxTodoCompletionIterations = 7
        });

        Assert.True(spec.ContinueUntilTodosComplete);
        Assert.Equal(7, spec.MaxTodoCompletionIterations);
        var clone = spec.Clone();
        Assert.True(clone.ContinueUntilTodosComplete);
        Assert.Equal(7, clone.MaxTodoCompletionIterations);
    }

    private static HarnessAgentOptions BuildHarnessAgentOptions(ResolvedCompactionStrategy? compaction)
    {
        var request = new AgentRunRequest
        {
            Agent = new AgentExecutionSpec(),
            ResolvedModel = new ServerModel(Guid.NewGuid(), "test-model"),
            Configuration = new AppChatConfiguration("test-model", []),
            Conversation = [],
            UserMessage = "Hello"
        };

        return AgenticRuntimeAgentFactory.BuildHarnessAgentOptions(
            request,
            AgenticToolSet.Empty,
            null!,
            hasRagContent: false,
            supportsFunctions: false,
            todoProfile: null,
            agentModeProfile: null,
            fileAccessProfile: null,
            workspaceStore: null,
            shellExecutor: null,
            NullLoggerFactory.Instance,
            compaction);
    }
}

#pragma warning restore MAAI001
