using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

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
            hasConfiguredKnowledge: false,
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
        var compactionProfileId = Guid.NewGuid();
        var spec = AgentExecutionSpecFactory.FromTemplate(new AgentTemplateDefinition
        {
            TodoProviderProfileId = todoProfileId,
            AgentModeProviderProfileId = modeProfileId,
            FileAccessProviderProfileId = fileAccessProfileId,
            CompactionProfileId = compactionProfileId
        });

        Assert.Equal(todoProfileId, spec.TodoProviderProfileId);
        Assert.Equal(modeProfileId, spec.AgentModeProviderProfileId);
        Assert.Equal(fileAccessProfileId, spec.FileAccessProviderProfileId);
        Assert.Equal(compactionProfileId, spec.CompactionProfileId);
    }

    [Fact]
    public void AgentTemplateClone_PreservesProviderSelections()
    {
        var todoProfileId = Guid.NewGuid();
        var modeProfileId = Guid.NewGuid();
        var fileAccessProfileId = Guid.NewGuid();
        var compactionProfileId = Guid.NewGuid();

        var clone = new AgentTemplateDefinition
        {
            TodoProviderProfileId = todoProfileId,
            AgentModeProviderProfileId = modeProfileId,
            FileAccessProviderProfileId = fileAccessProfileId,
            CompactionProfileId = compactionProfileId
        }.Clone();

        Assert.Equal(todoProfileId, clone.TodoProviderProfileId);
        Assert.Equal(modeProfileId, clone.AgentModeProviderProfileId);
        Assert.Equal(fileAccessProfileId, clone.FileAccessProviderProfileId);
        Assert.Equal(compactionProfileId, clone.CompactionProfileId);
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

    [Fact]
    public void BuildContextProviders_AddsRagForConfiguredTemporaryAgent()
    {
        var request = new AgentRunRequest
        {
            Agent = new AgentExecutionSpec { Id = Guid.Empty, KnowledgeStoreIds = [Guid.NewGuid()] },
            ResolvedModel = new ServerModel(Guid.NewGuid(), "test-model"),
            Configuration = new AppChatConfiguration("test-model", []),
            Conversation = [],
            UserMessage = "Hello"
        };

        var providers = AgenticRuntimeAgentFactory.BuildContextProviders(
            request,
            Mock.Of<IKnowledgeSearchService>(),
            hasConfiguredKnowledge: true,
            supportsFunctions: true,
            loggerFactory: NullLoggerFactory.Instance,
            todoProfile: null);

        Assert.Single(providers);
        Assert.IsType<TextSearchProvider>(providers[0]);
    }

    [Fact]
    public void BuildHarnessAgentOptions_EnablesOnDemandRagToolsForConfiguredKnowledge()
    {
        var options = BuildHarnessAgentOptions(null, hasConfiguredKnowledge: true, supportsFunctions: true);
        var chatOptions = Assert.IsType<ChatOptions>(options.ChatOptions);

        AgenticRuntimeAgentFactory.ConfigureToolMode(
            chatOptions,
            AgenticToolSet.Empty,
            hasConfiguredKnowledge: true,
            supportsFunctions: true,
            shellExecutor: null);

        Assert.True(chatOptions.AllowMultipleToolCalls);
        Assert.Equal(ChatToolMode.Auto, chatOptions.ToolMode);
        Assert.IsType<TextSearchProvider>(Assert.Single(options.AIContextProviders!));
        Assert.Equal(
            TextSearchProviderOptions.TextSearchBehavior.OnDemandFunctionCalling,
            AgenticRuntimeAgentFactory.ResolveRagSearchBehavior(supportsFunctions: true));
    }

    [Fact]
    public void ResolveRagSearchBehavior_UsesBeforeInvokeWithoutFunctionCalling()
    {
        Assert.Equal(
            TextSearchProviderOptions.TextSearchBehavior.BeforeAIInvoke,
            AgenticRuntimeAgentFactory.ResolveRagSearchBehavior(supportsFunctions: false));
    }

    [Fact]
    public void BuildChatHistoryProvider_ExcludesOnlyAiContextMessagesAndKeepsCompactionReducer()
    {
        var aiContextMessage = new ChatMessage(ChatRole.User, "retrieved")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, "rag");

        Assert.False(AgenticRuntimeAgentFactory.ShouldStoreChatHistoryMessage(aiContextMessage));
        Assert.True(AgenticRuntimeAgentFactory.ShouldStoreChatHistoryMessage(new ChatMessage(ChatRole.User, "user")));
        Assert.True(AgenticRuntimeAgentFactory.ShouldStoreChatHistoryMessage(new ChatMessage(ChatRole.Assistant, "assistant")));

        Assert.Null(AgenticRuntimeAgentFactory.BuildChatHistoryProvider(null).ChatReducer);

        var strategy = new ContextWindowCompactionStrategy(16_000, 2_000, 0.5, 0.8);
        var provider = AgenticRuntimeAgentFactory.BuildChatHistoryProvider(new ResolvedCompactionStrategy(
            strategy,
            new CompactionBudget(16_000, 2_000, 14_000),
            []));
        Assert.NotNull(provider.ChatReducer);
    }

    [Theory]
    [InlineData("Architecture", "system-design.md", "Load balancing", "Architecture / system-design.md / Load balancing")]
    [InlineData("Architecture", "system-design.md", null, "Architecture / system-design.md")]
    public void BuildKnowledgeSourceName_IncludesSectionWhenAvailable(
        string storeName,
        string fileName,
        string? section,
        string expected)
    {
        Assert.Equal(expected, AgenticRuntimeAgentFactory.BuildKnowledgeSourceName(new RagSearchResult
        {
            KnowledgeStoreName = storeName,
            FileName = fileName,
            Section = section
        }));
    }

    [Fact]
    public async Task SearchAgentKnowledgeAsync_UsesCurrentResultsAndOnlyAttachedStores()
    {
        var attachedStoreId = Guid.NewGuid();
        var search = new Mock<IKnowledgeSearchService>(MockBehavior.Strict);
        var call = 0;
        search.Setup(service => service.SearchAsync(
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { attachedStoreId })),
                It.IsAny<string>(),
                5,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++call == 1
                ? new RagSearchResponse()
                : new RagSearchResponse
                {
                    Results = [new RagSearchResult { Content = "indexed after agent creation" }]
                });

        var first = await AgenticRuntimeAgentFactory.SearchAgentKnowledgeAsync(
            [attachedStoreId], search.Object, "fact", TestContext.Current.CancellationToken);
        var second = await AgenticRuntimeAgentFactory.SearchAgentKnowledgeAsync(
            [attachedStoreId], search.Object, "fact", TestContext.Current.CancellationToken);

        Assert.Empty(first);
        Assert.Equal("indexed after agent creation", Assert.Single(second).Content);
        search.VerifyAll();
    }

    private static HarnessAgentOptions BuildHarnessAgentOptions(
        ResolvedCompactionStrategy? compaction,
        bool hasConfiguredKnowledge = false,
        bool supportsFunctions = false)
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
            hasConfiguredKnowledge ? Mock.Of<IKnowledgeSearchService>() : null!,
            hasConfiguredKnowledge,
            supportsFunctions,
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
