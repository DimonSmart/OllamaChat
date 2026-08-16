using ChatClient.Api.Client.Pages;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services;
using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Application.Services.Sandbox;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using AgentModeProviderOptions = Microsoft.Agents.AI.AgentModeProviderOptions;
using AgentSession = Microsoft.Agents.AI.AgentSession;
using AIAgent = Microsoft.Agents.AI.AIAgent;
using HarnessAgentOptions = Microsoft.Agents.AI.HarnessAgentOptions;
using TodoProvider = Microsoft.Agents.AI.TodoProvider;

namespace ChatClient.Tests;

public sealed class UnifiedAgentRuntimeChatSessionServiceTests
{
    [Theory]
    [InlineData("todos_add", true)]
    [InlineData("todos_complete", true)]
    [InlineData("todos_remove", true)]
    [InlineData("mode_set", true)]
    [InlineData("mcp_search", false)]
    public void ChangesHarnessSessionState_RecognizesOnlyStateChangingProviderTools(
        string toolName,
        bool expected)
    {
        var completed = new HarnessToolCallCompleted(
            "call", toolName, toolName, "built-in", "Harness", null, false, "{}", "ok",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.Equal(expected, UnifiedAgentRuntimeChatSessionService.ChangesHarnessSessionState(completed));
    }

    [Theory]
    [InlineData("C:\\Project", "C:\\Project\\", true)]
    [InlineData("C:\\Project", "C:\\Other", false)]
    public void HaveSameWorkspace_NormalizesTrailingSeparators(string fileAccessWorkspace, string sandboxWorkspace, bool expected)
    {
        Assert.Equal(expected, UnifiedAgentRuntimeChatSessionService.HaveSameWorkspace(fileAccessWorkspace, sandboxWorkspace));
    }

    [Fact]
    public async Task DirectHarness_ReusesSessionForTwoTurnsAndResetStartsFreshConversation()
    {
        var fixture = CreateDirectFixture();
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        var firstConversationId = fixture.Service.Id;

        await fixture.Service.SendAsync("first", cancellationToken: TestContext.Current.CancellationToken);
        await fixture.Service.SendAsync("second", [
            new AppChatMessageFile("notes.txt", 5, "text/plain", Encoding.UTF8.GetBytes("notes")),
            new AppChatMessageFile("pixel.png", 3, "image/png", [1, 2, 3])
        ], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.ChatClient.Requests.Count);
        Assert.Equal("first", CurrentUserText(fixture.ChatClient.Requests[0].Messages));
        Assert.Equal("secondnotes", CurrentUserText(fixture.ChatClient.Requests[1].Messages));
        Assert.Contains(
            fixture.ChatClient.Requests[1].Messages.SelectMany(static message => message.Contents),
            static content => content is DataContent data && data.MediaType == "image/png");
        Assert.Equal("test-model", fixture.ChatClient.Requests[1].Options?.ModelId);
        Assert.Equal(0.35f, fixture.ChatClient.Requests[1].Options?.Temperature);
        Assert.Equal(1.15, fixture.ChatClient.Requests[1].Options?.AdditionalProperties?["repeat_penalty"]);
        Assert.Contains(
            fixture.ChatClient.Requests[1].Messages,
            static message => message.Role == ChatRole.Assistant && message.Text == "answer-1");

        await fixture.Service.ResetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(firstConversationId, fixture.Service.Id);
        Assert.Empty(fixture.Service.Messages);
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        await fixture.Service.SendAsync("fresh", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("fresh", CurrentUserText(fixture.ChatClient.Requests[2].Messages));
        Assert.DoesNotContain(
            fixture.ChatClient.Requests[2].Messages,
            static message => message.Role == ChatRole.Assistant && message.Text == "answer-1");
    }

    [Fact]
    public async Task StartAsync_ExposesActiveSessionUntilReset()
    {
        var fixture = CreateDirectFixture();

        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(fixture.Service.HasActiveSession);
        var activeSession = Assert.IsType<ActiveChatSessionInfo>(fixture.Service.ActiveSession);
        Assert.Equal(fixture.Request.RuntimeReference, activeSession.RuntimeReference);
        Assert.Equal(fixture.Request.RuntimeDefaultModel, activeSession.Model);

        await fixture.Service.ResetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(fixture.Service.HasActiveSession);
        Assert.Null(fixture.Service.ActiveSession);
    }

    [Fact]
    public async Task HarnessSession_RoundTripsWithoutInvokingTheModelDuringRestore()
    {
        var fixture = CreateDirectFixture();
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        await fixture.Service.SendAsync("before export", cancellationToken: TestContext.Current.CancellationToken);

        var snapshot = await fixture.Service.ExportHarnessSessionAsync(TestContext.Current.CancellationToken);
        var callsBeforeRestore = fixture.ChatClient.Requests.Count;

        await fixture.Service.RestoreHarnessSessionAsync(snapshot, TestContext.Current.CancellationToken);

        Assert.Equal(callsBeforeRestore, fixture.ChatClient.Requests.Count);
        await fixture.Service.SendAsync("after restore", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(
            fixture.ChatClient.Requests[^1].Messages,
            static message => message.Role == ChatRole.Assistant && message.Text == "answer-1");
    }

    [Fact]
    public async Task RestoreHarnessSessionAsync_RejectsChangedMcpBindingsWithoutReplacingTheSession()
    {
        var fixture = CreateDirectFixture(mcpBindings:
        [
            new McpServerSessionBinding
            {
                BindingId = Guid.NewGuid(),
                ServerName = "github",
                Enabled = true,
                SelectAllTools = false,
                SelectedTools = ["search", "fetch"],
                Roots = ["C:\\Workspace"],
                Parameters = new Dictionary<string, string?> { ["token"] = "one" }
            }
        ]);
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = await fixture.Service.ExportHarnessSessionAsync(TestContext.Current.CancellationToken);
        var originalAgent = GetPrivateField<AIAgent>(fixture.Service, "_directAgent");

        var requestSnapshot = fixture.Request.Snapshot();
        var changedParameters = new ChatEngineSessionStartRequest
        {
            Configuration = requestSnapshot.Configuration,
            Agents = requestSnapshot.Agents,
            RuntimeParticipant = requestSnapshot.RuntimeParticipant,
            RuntimeReference = requestSnapshot.RuntimeReference,
            RuntimeDefaultModel = requestSnapshot.RuntimeDefaultModel,
            RuntimeInputs = requestSnapshot.RuntimeInputs,
            Overrides = new AgentSessionOverrides
            {
                McpServerBindings = [new McpServerSessionBinding { ServerName = "filesystem" }]
            }
        };
        SetPrivateField(fixture.Service, "_parameters", changedParameters);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.RestoreHarnessSessionAsync(snapshot, TestContext.Current.CancellationToken));

        Assert.Contains("different MCP bindings", exception.Message);
        Assert.Same(originalAgent, GetPrivateField<AIAgent>(fixture.Service, "_directAgent"));
    }

    [Fact]
    public async Task AgenticChatPageDispose_DoesNotCancelOrResetActiveSession()
    {
        var chatService = new Mock<IChatEngineSessionService>();
        var viewModelService = new Mock<IAgenticChatViewModelService>();
        var page = new TestAgenticChatPage(chatService.Object, viewModelService.Object);

        await page.DisposeAsync();

        chatService.Verify(service => service.CancelAsync(), Times.Never);
        chatService.Verify(service => service.ResetAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetSessionStateAsync_ProjectsConfiguredDirectSessionProviders()
    {
        var fixture = CreateDirectFixture(withSessionStateProviders: true);

        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);

        var state = await fixture.Service.GetSessionStateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(state);
        Assert.True(state.HasTodoProvider);
        Assert.True(state.HasAgentModeProvider);
        Assert.Equal("Plan", state.Mode);
        Assert.Equal(["Plan"], state.AvailableModes);
        Assert.Empty(state.Todos);
    }

    [Fact]
    public async Task SetAgentModeAsync_ChangesExistingDirectSessionBeforeFirstMessageWithoutInvocation()
    {
        var fixture = CreateDirectFixture(availableModes: ["Plan", "Execute"]);

        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        await fixture.Service.SetAgentModeAsync("Execute", cancellationToken: TestContext.Current.CancellationToken);

        var state = await fixture.Service.GetSessionStateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(state);
        Assert.Equal("Execute", state.Mode);
        Assert.Equal(["Plan", "Execute"], state.AvailableModes);
        Assert.Empty(fixture.Service.Messages);
        Assert.Empty(fixture.ChatClient.Requests);
    }

    [Fact]
    public async Task SetAgentModeAsync_PersistsForSubsequentTurnsAndNewChatUsesProfileDefault()
    {
        var fixture = CreateDirectFixture(availableModes: ["Plan", "Execute"]);

        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        await fixture.Service.SetAgentModeAsync("Execute", cancellationToken: TestContext.Current.CancellationToken);
        await fixture.Service.SendAsync("first", cancellationToken: TestContext.Current.CancellationToken);
        await fixture.Service.SendAsync("second", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Execute", (await fixture.Service.GetSessionStateAsync(cancellationToken: TestContext.Current.CancellationToken))!.Mode);
        Assert.Equal(2, fixture.ChatClient.Requests.Count);

        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Plan", (await fixture.Service.GetSessionStateAsync(cancellationToken: TestContext.Current.CancellationToken))!.Mode);
    }

    [Fact]
    public async Task SetAgentModeAsync_RejectsUnavailableModeWithoutChangingRuntimeState()
    {
        var fixture = CreateDirectFixture(availableModes: ["Research", "Verification"]);

        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.SetAgentModeAsync("Execute", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("not available", exception.Message);
        Assert.Equal("Research", (await fixture.Service.GetSessionStateAsync(cancellationToken: TestContext.Current.CancellationToken))!.Mode);
        Assert.Empty(fixture.Service.Messages);
        Assert.Empty(fixture.ChatClient.Requests);
    }

    [Fact]
    public async Task SetAgentModeAsync_RejectsModeChangeWhileToolApprovalIsPending()
    {
        var fixture = CreateDirectFixture(availableModes: ["Plan", "Execute"]);
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        SetPendingApproval(fixture.Service);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.SetAgentModeAsync("Execute", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("Plan", (await fixture.Service.GetSessionStateAsync(cancellationToken: TestContext.Current.CancellationToken))!.Mode);
    }

    [Fact]
    public async Task DirectHarness_ToolApprovalUsesFrameworkRulesAndPreservesSessionState()
    {
        var fixture = CreateDirectFixture(availableModes: ["Plan", "Execute"]);
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);

        var testHarness = new ApprovalHarnessFixture();
        InstallDirectHarness(fixture.Service, testHarness.Agent, testHarness.Session, ["Plan", "Execute"]);
        await fixture.Service.SetAgentModeAsync("Execute", cancellationToken: TestContext.Current.CancellationToken);
        var stateBeforeApproval = (await fixture.Service.GetSessionStateAsync(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.True(stateBeforeApproval.HasTodoProvider);
        Assert.Empty(stateBeforeApproval.Todos);

        await fixture.Service.SendAsync("A", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(fixture.Service.PendingToolApproval);
        Assert.Equal(0, testHarness.InvocationCount);
        Assert.False(fixture.Service.IsAnswering);
        Assert.False(fixture.Service.RequiresReset);
        var stateAfterApproval = (await fixture.Service.GetSessionStateAsync(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal("Execute", stateAfterApproval.Mode);
        Assert.True(stateAfterApproval.HasTodoProvider);
        Assert.Empty(stateAfterApproval.Todos);

        await fixture.Service.RespondToToolApprovalAsync(ToolApprovalDecision.ApproveOnce, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, testHarness.InvocationCount);
        Assert.Null(fixture.Service.PendingToolApproval);
        Assert.False(fixture.Service.RequiresReset);
        Assert.Equal("Execute", (await fixture.Service.GetSessionStateAsync(cancellationToken: TestContext.Current.CancellationToken))!.Mode);

        await fixture.Service.SendAsync("Deny", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(fixture.Service.PendingToolApproval);
        await fixture.Service.RespondToToolApprovalAsync(ToolApprovalDecision.Deny, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, testHarness.InvocationCount);
        Assert.Null(fixture.Service.PendingToolApproval);
        Assert.False(fixture.Service.RequiresReset);

        await fixture.Service.SendAsync("A", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(fixture.Service.PendingToolApproval);
        await fixture.Service.RespondToToolApprovalAsync(ToolApprovalDecision.ApproveForSession, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, testHarness.InvocationCount);

        await fixture.Service.SendAsync("B", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(fixture.Service.PendingToolApproval);
        await fixture.Service.RespondToToolApprovalAsync(ToolApprovalDecision.ApproveOnce, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, testHarness.InvocationCount);

        await fixture.Service.ResetAsync(cancellationToken: TestContext.Current.CancellationToken);
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        var resetHarness = new ApprovalHarnessFixture();
        InstallDirectHarness(fixture.Service, resetHarness.Agent, resetHarness.Session, ["Plan", "Execute"]);

        await fixture.Service.SendAsync("A", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(fixture.Service.PendingToolApproval);
        Assert.Equal(0, resetHarness.InvocationCount);
    }

    [Fact]
    public async Task RespondToToolApprovalAsync_RejectsInvalidDecisionBeforeChangingRuntimeState()
    {
        var service = CreateService(new StubAgentRunner([]));
        SetPendingApproval(service);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.RespondToToolApprovalAsync((ToolApprovalDecision)999, cancellationToken: TestContext.Current.CancellationToken));

        Assert.NotNull(service.PendingToolApproval);
        Assert.False(service.IsAnswering);
    }

    [Fact]
    public async Task GetSessionStateAsync_ReturnsNullForDirectAgentWithoutProviders()
    {
        var fixture = CreateDirectFixture();

        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await fixture.Service.GetSessionStateAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetSessionStateAsync_ProjectsResolvedCompactionWithoutRuntimeStrategy()
    {
        var fixture = CreateDirectFixture();
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        typeof(UnifiedAgentRuntimeChatSessionService)
            .GetField("_directCompaction", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Service, new AgentSessionCompactionViewModel("Balanced", 120_000, "Context window: tool results 50%, history 80%"));

        var state = await fixture.Service.GetSessionStateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(state);
        Assert.Equal("Balanced", state.Compaction!.ProfileName);
        Assert.Equal(120_000, state.Compaction.InputBudgetTokens);
    }

    [Fact]
    public async Task SendAsync_ProjectsParticipantStreamsByRuntimeMessageId()
    {
        var runner = new StubAgentRunner([
            new AgentTextDelta("m1", "Planner", "plan"),
            new AgentTextDelta("m2", "Writer", "draft"),
            new AgentMessageCompleted("m1", new AgentOutputMessage("Planner", "plan")),
            new AgentMessageCompleted("m2", new AgentOutputMessage("Writer", "draft")),
            new AgentRunCompleted(new AgentRunResult
            {
                FinalMessage = new AgentOutputMessage("Workflow", "summary"),
                FinalMessageId = "summary",
                Messages =
                [
                    new AgentOutputMessage("Planner", "plan"),
                    new AgentOutputMessage("Writer", "draft")
                ]
            })
        ]);
        var service = CreateService(runner);
        await service.StartAsync(CreateStartRequest(), cancellationToken: TestContext.Current.CancellationToken);

        await service.SendAsync("go", cancellationToken: TestContext.Current.CancellationToken);

        var assistants = service.Messages
            .Where(static message => message.Role == AppChatRole.Assistant)
            .ToList();
        Assert.Equal(3, assistants.Count);
        Assert.Contains(assistants, message => message.AgentName == "Planner" && message.Content == "plan");
        Assert.Contains(assistants, message => message.AgentName == "Writer" && message.Content == "draft");
        Assert.Contains(assistants, message => message.AgentName == "Workflow" && message.Content == "summary");
    }

    [Fact]
    public async Task SendAsync_DoesNotDuplicateFinalMessageWhenFinalMessageIdReferencesCompletedOutput()
    {
        var runner = new StubAgentRunner([
            new AgentTextDelta("m1", "Agent", "answer"),
            new AgentMessageCompleted("m1", new AgentOutputMessage("Agent", "answer")),
            new AgentRunCompleted(new AgentRunResult
            {
                FinalMessage = new AgentOutputMessage("Agent", "answer"),
                FinalMessageId = "m1",
                Messages = [new AgentOutputMessage("Agent", "answer")]
            })
        ]);
        var service = CreateService(runner);
        await service.StartAsync(CreateStartRequest(), cancellationToken: TestContext.Current.CancellationToken);

        await service.SendAsync("go", cancellationToken: TestContext.Current.CancellationToken);

        var assistant = Assert.Single(
            service.Messages,
            static message => message.Role == AppChatRole.Assistant);
        Assert.Equal("answer", assistant.Content);
        Assert.Equal("Agent", assistant.AgentName);
    }

    [Theory]
    [MemberData(nameof(CompletedContentCases))]
    public async Task SendAsync_CompletedMessageReplacesStreamWithSameRuntimeMessageId(
        IReadOnlyList<AgentRunEvent> messageEvents,
        string expectedContent)
    {
        var events = messageEvents
            .Concat([
                new AgentRunCompleted(new AgentRunResult
                {
                    FinalMessage = new AgentOutputMessage("Agent", expectedContent),
                    FinalMessageId = "m1",
                    Messages = [new AgentOutputMessage("Agent", expectedContent)]
                })
            ])
            .ToList();
        var service = CreateService(new StubAgentRunner(events));
        await service.StartAsync(CreateStartRequest(), cancellationToken: TestContext.Current.CancellationToken);

        await service.SendAsync("go", cancellationToken: TestContext.Current.CancellationToken);

        var assistant = Assert.Single(
            service.Messages,
            static message => message.Role == AppChatRole.Assistant);
        Assert.Equal(expectedContent, assistant.Content);
        Assert.Equal("Agent", assistant.AgentName);
        Assert.False(assistant.IsStreaming);
    }

    [Fact]
    public async Task SendAsync_CompletedRunFinalizesRemainingStreams()
    {
        var service = CreateService(new StubAgentRunner([
            new AgentTextDelta("m1", "Agent", "answer"),
            new AgentRunCompleted(new AgentRunResult
            {
                FinalMessage = new AgentOutputMessage("Agent", "answer"),
                FinalMessageId = "m1",
                Messages = [new AgentOutputMessage("Agent", "answer")]
            })
        ]));
        await service.StartAsync(CreateStartRequest(), cancellationToken: TestContext.Current.CancellationToken);

        await service.SendAsync("go", cancellationToken: TestContext.Current.CancellationToken);

        var assistant = Assert.Single(
            service.Messages,
            static message => message.Role == AppChatRole.Assistant);
        Assert.Equal("answer", assistant.Content);
        Assert.False(assistant.IsStreaming);
        Assert.False(service.IsAnswering);
    }

    [Fact]
    public async Task SendAsync_FailedRunCancelsStreamsAndAddsOneErrorMessage()
    {
        var service = CreateService(new StubAgentRunner([
            new AgentTextDelta("m1", "Agent", "partial"),
            new AgentRunFailed(new AgentRunError("execution_failed", "boom", true))
        ]));
        await service.StartAsync(CreateStartRequest(), cancellationToken: TestContext.Current.CancellationToken);

        await service.SendAsync("go", cancellationToken: TestContext.Current.CancellationToken);

        var assistants = service.Messages
            .Where(static message => message.Role == AppChatRole.Assistant)
            .ToList();
        Assert.Equal(2, assistants.Count);
        Assert.Single(assistants, static message => message.IsCanceled && !message.IsStreaming);
        Assert.Single(assistants, static message => message.Content == "Agent runtime error: boom");
        Assert.False(service.IsAnswering);
    }

    [Fact]
    public async Task CancelAsync_CancelsStreamsWithoutGenericError()
    {
        var runner = new BlockingAgentRunner();
        var service = CreateService(runner);
        await service.StartAsync(CreateStartRequest(), cancellationToken: TestContext.Current.CancellationToken);

        var sendTask = service.SendAsync("go", cancellationToken: TestContext.Current.CancellationToken);
        await runner.WaitUntilStreamingAsync();

        await service.CancelAsync();
        await sendTask;

        var assistant = Assert.Single(
            service.Messages,
            static message => message.Role == AppChatRole.Assistant);
        Assert.True(assistant.IsCanceled);
        Assert.False(assistant.IsStreaming);
        Assert.DoesNotContain(
            service.Messages,
            static message => message.Content.StartsWith("Agent runtime error:", StringComparison.Ordinal));
        Assert.False(service.IsAnswering);
        Assert.True(service.RequiresReset);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync("must not continue", cancellationToken: TestContext.Current.CancellationToken));

        var canceledConversationId = service.Id;
        await service.ResetAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(canceledConversationId, service.Id);
        Assert.Empty(service.Messages);
        Assert.False(service.RequiresReset);
    }

    [Fact]
    public async Task SendAsync_ForwardsCurrentUserAttachmentsToRuntimeRequest()
    {
        var runner = new StubAgentRunner([
            new AgentRunCompleted(new AgentRunResult
            {
                FinalMessage = new AgentOutputMessage("Agent", "done"),
                FinalMessageId = "final",
                Messages = [new AgentOutputMessage("Agent", "done")]
            })
        ]);
        var service = CreateService(runner);
        await service.StartAsync(CreateStartRequest(), cancellationToken: TestContext.Current.CancellationToken);
        var file = new AppChatMessageFile(
            "notes.md",
            7,
            "text/markdown",
            Encoding.UTF8.GetBytes("# Notes"));

        await service.SendAsync("go", [file], cancellationToken: TestContext.Current.CancellationToken);

        var attachment = Assert.Single(runner.LastRequest!.Attachments);
        Assert.Equal("notes.md", attachment.Name);
        Assert.Equal("text/markdown", attachment.ContentType);
        Assert.Equal("# Notes", attachment.Content);
        Assert.Equal(file.Data, attachment.Data);
    }

    [Fact]
    public async Task SendAsync_ForwardsRuntimeInputsToRuntimeRequest()
    {
        var runner = new StubAgentRunner([
            new AgentRunCompleted(new AgentRunResult
            {
                FinalMessage = new AgentOutputMessage("Agent", "done"),
                FinalMessageId = "final",
                Messages = [new AgentOutputMessage("Agent", "done")]
            })
        ]);
        var service = CreateService(runner);
        var request = new ChatEngineSessionStartRequest
        {
            Configuration = new AppChatConfiguration("model", []),
            Agents = [],
            RuntimeReference = new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, "workflow"),
            RuntimeInputs = new Dictionary<string, string>
            {
                ["topic"] = "runtime design",
                ["strict"] = "True"
            }
        };
        await service.StartAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        await service.SendAsync("go", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("runtime design", runner.LastRequest!.Inputs["topic"]);
        Assert.Equal("True", runner.LastRequest.Inputs["strict"]);
    }

    [Fact]
    public async Task StartAsync_RejectsParallelStartupWithoutCreatingSecondSandbox()
    {
        var sandboxFactory = new BlockingSandboxSessionFactory();
        var service = CreateService(new StubAgentRunner([]), sandboxFactory, CreateSandboxCatalog());
        var request = CreateSandboxStartRequest();

        var firstStart = service.StartAsync(request, cancellationToken: TestContext.Current.CancellationToken);
        await sandboxFactory.WaitUntilCalledAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(request, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("startup is already in progress", exception.Message);
        Assert.Equal(1, sandboxFactory.CallCount);

        sandboxFactory.Complete();
        await firstStart;

        var state = await service.GetSessionStateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(state?.Sandbox);
        Assert.False(service.RequiresReset);
    }

    [Fact]
    public async Task StartAsync_PassesCurrentChatSessionIdToSandboxFactory()
    {
        var sandboxFactory = new BlockingSandboxSessionFactory();
        var service = CreateService(new StubAgentRunner([]), sandboxFactory, CreateSandboxCatalog());

        var startTask = service.StartAsync(CreateSandboxStartRequest(), cancellationToken: TestContext.Current.CancellationToken);
        await sandboxFactory.WaitUntilCalledAsync();

        Assert.Equal(service.Id.ToString("N"), sandboxFactory.SessionIds.Single());

        sandboxFactory.Complete();
        await startTask;
    }

    [Fact]
    public async Task StartAsync_ReleasesStartupGateAfterSandboxFailureAndAllowsRetry()
    {
        var sandboxFactory = new FailThenSucceedSandboxSessionFactory();
        var service = CreateService(new StubAgentRunner([]), sandboxFactory, CreateSandboxCatalog());
        var request = CreateSandboxStartRequest();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(request, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Null(await service.GetSessionStateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(service.RequiresReset);

        await service.StartAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        var state = await service.GetSessionStateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(state?.Sandbox);
        Assert.Equal(2, sandboxFactory.CallCount);
    }

    private static UnifiedAgentRuntimeChatSessionService CreateService(
        IAgentRunner runner,
        ISandboxSessionFactory? sandboxSessionFactory = null,
        IAgentDefinitionCatalog? definitionCatalog = null) =>
        new(
            runner,
            definitionCatalog ?? new StubDefinitionCatalog(),
            new AgentRunContextFactory(),
            new AgenticChatEngineStreamingBridge(),
            NullLogger<UnifiedAgentRuntimeChatSessionService>.Instance,
            null!,
            sandboxSessionFactory ?? new StubSandboxSessionFactory(),
            null!,
            new HarnessResponseEventProjector(NullLogger<HarnessResponseEventProjector>.Instance));

    private static void SetPendingApproval(UnifiedAgentRuntimeChatSessionService service)
    {
        var request = new ToolApprovalRequestContent(
            "request-1",
            new FunctionCallContent(
                "call-1",
                "protected_operation",
                new Dictionary<string, object?> { ["value"] = "A" }));
        typeof(UnifiedAgentRuntimeChatSessionService)
            .GetField("_pendingToolApprovalRequest", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, request);
        typeof(UnifiedAgentRuntimeChatSessionService)
            .GetProperty(nameof(IChatEngineSessionService.PendingToolApproval), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(service, new ToolApprovalRequestViewModel("request-1", "protected_operation", "{\"value\":\"A\"}"));
    }

    private static void InstallDirectHarness(
        UnifiedAgentRuntimeChatSessionService service,
        AIAgent agent,
        AgentSession session,
        IReadOnlyList<string> availableModes)
    {
        typeof(UnifiedAgentRuntimeChatSessionService)
            .GetField("_directAgent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, agent);
        typeof(UnifiedAgentRuntimeChatSessionService)
            .GetField("_directSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, session);
        typeof(UnifiedAgentRuntimeChatSessionService)
            .GetField("_directAvailableModes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, availableModes);
    }

    private static DirectFixture CreateDirectFixture(
        bool withSessionStateProviders = false,
        IReadOnlyList<string>? availableModes = null,
        IReadOnlyList<McpServerSessionBinding>? mcpBindings = null)
    {
        var templateId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var template = new AgentTemplateDefinition
        {
            Id = templateId,
            AgentName = "Harness test agent",
            Content = "Answer deterministically.",
            Temperature = 0.35,
            RepeatPenalty = 1.15
        };
        if (withSessionStateProviders || availableModes is not null)
        {
            template.TodoProviderProfileId = Guid.NewGuid();
            template.AgentModeProviderProfileId = Guid.NewGuid();
        }
        var model = new ServerModel(serverId, "test-model");
        var chatClient = new RecordingChatClient();

        var templateService = new Mock<IAgentTemplateService>(MockBehavior.Strict);
        templateService.Setup(service => service.GetByIdAsync(templateId)).ReturnsAsync(template);
        var serverService = new Mock<ILlmServerConfigService>(MockBehavior.Strict);
        serverService.Setup(service => service.GetByIdAsync(serverId)).ReturnsAsync(new LlmServerConfig
        {
            Id = serverId,
            Name = "Test server"
        });
        var clientFactory = new Mock<ILlmChatClientFactory>(MockBehavior.Strict);
        clientFactory.Setup(factory => factory.CreateAsync(model, It.IsAny<CancellationToken>()))
            .ReturnsAsync(chatClient);
        var capabilities = new Mock<IModelCapabilityService>(MockBehavior.Strict);
        capabilities.Setup(service => service.SupportsFunctionCallingAsync(model, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var tools = new Mock<IAppToolCatalog>(MockBehavior.Strict);
        var interaction = new Mock<IMcpUserInteractionService>(MockBehavior.Strict);
        var rag = new Mock<IKnowledgeSearchService>(MockBehavior.Strict);
        var todoProfiles = new Mock<ITodoProviderProfileService>(MockBehavior.Strict);
        var agentModeProfiles = new Mock<IAgentModeProviderProfileService>(MockBehavior.Strict);
        if (withSessionStateProviders || availableModes is not null)
        {
            todoProfiles.Setup(service => service.GetByIdAsync(template.TodoProviderProfileId!.Value))
                .ReturnsAsync(new TodoProviderProfile { Name = "Todos" });
            agentModeProfiles.Setup(service => service.GetByIdAsync(template.AgentModeProviderProfileId!.Value))
                .ReturnsAsync(new AgentModeProviderProfile
                {
                    Name = "Modes",
                    DefaultMode = availableModes?.FirstOrDefault() ?? "Plan",
                    Modes = (availableModes ?? ["Plan"])
                        .Select(mode => new AgentModeProfile { Name = mode, Instructions = $"{mode} work." })
                        .ToList()
                });
        }
        rag.Setup(service => service.HasReadyContentAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var runtimeFactory = new AgenticRuntimeAgentFactory(
            serverService.Object,
            clientFactory.Object,
            capabilities.Object,
            tools.Object,
            interaction.Object,
            rag.Object,
            todoProfiles.Object,
            agentModeProfiles.Object,
            Options.Create(new AgenticToolInvocationPolicyOptions()),
            NullLogger<AgenticRuntimeAgentFactory>.Instance,
            NullLoggerFactory.Instance);
        var service = new UnifiedAgentRuntimeChatSessionService(
            new StubAgentRunner([]),
            new StubDefinitionCatalog(),
            new AgentRunContextFactory(),
            new AgenticChatEngineStreamingBridge(),
            NullLogger<UnifiedAgentRuntimeChatSessionService>.Instance,
            templateService.Object,
            new StubSandboxSessionFactory(),
            runtimeFactory,
            new HarnessResponseEventProjector(NullLogger<HarnessResponseEventProjector>.Instance));
        var request = new ChatEngineSessionStartRequest
        {
            Configuration = new AppChatConfiguration("test-model", []),
            Agents = [],
            RuntimeReference = new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, templateId.ToString()),
            RuntimeDefaultModel = model,
            Overrides = new AgentSessionOverrides { McpServerBindings = mcpBindings }
        };

        return new DirectFixture(service, request, chatClient);
    }

    private static string CurrentUserText(IReadOnlyList<ChatMessage> messages) =>
        string.Concat(messages.Last(static message => message.Role == ChatRole.User)
            .Contents.OfType<TextContent>().Select(static content => content.Text));

    private static T GetPrivateField<T>(object instance, string name) where T : class =>
        Assert.IsAssignableFrom<T>(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance));

    private static void SetPrivateField(object instance, string name, object? value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private sealed record DirectFixture(
        UnifiedAgentRuntimeChatSessionService Service,
        ChatEngineSessionStartRequest Request,
        RecordingChatClient ChatClient);

    private sealed class TestAgenticChatPage : AgenticChatPageBase
    {
        public TestAgenticChatPage(
            IChatEngineSessionService chatService,
            IAgenticChatViewModelService viewModelService)
        {
            ChatService = chatService;
            ChatViewModelService = viewModelService;
        }

        protected override Task LoadAgentsAsync() => Task.CompletedTask;

        protected override Task LoadUserSettingsAsync() => Task.CompletedTask;
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public List<RecordedChatRequest> Requests { get; } = [];

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata)
                ? new ChatClientMetadata("test", null, "test-model")
                : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "unused")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new RecordedChatRequest(messages.Select(static message => message.Clone()).ToList(), options));
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"answer-{Requests.Count}");
        }
    }

    private sealed record RecordedChatRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);

    private sealed class ApprovalHarnessFixture
    {
        private readonly ApprovalChatClient _chatClient = new();

#pragma warning disable MAAI001
        public ApprovalHarnessFixture()
        {
            var protectedOperation = AIFunctionFactory.Create(
                (string value) =>
                {
                    InvocationCount++;
                    return $"executed:{value}";
                },
                "protected_operation",
                "Test-only operation with an observable side effect.");
            Agent = _chatClient.AsHarnessAgent(new HarnessAgentOptions
            {
                ChatOptions = new ChatOptions
                {
                    Tools = [new ApprovalRequiredAIFunction(protectedOperation)],
                    ToolMode = ChatToolMode.Auto,
                    AllowMultipleToolCalls = false
                },
                DisableTodoProvider = true,
                AIContextProviders = [new TodoProvider()],
                DisableAgentModeProvider = false,
                AgentModeProviderOptions = new AgentModeProviderOptions
                {
                    DefaultMode = "Plan",
                    Modes =
                    [
                        new AgentModeProviderOptions.AgentMode("Plan", "Plan work."),
                        new AgentModeProviderOptions.AgentMode("Execute", "Execute work.")
                    ]
                },
                DisableWebSearch = true,
                DisableFileMemory = true,
                DisableAgentSkillsProvider = true,
                DisableCompaction = true
            });
            Session = Agent.CreateSessionAsync().GetAwaiter().GetResult();
        }
#pragma warning restore MAAI001

        public AIAgent Agent { get; }

        public AgentSession Session { get; }

        public int InvocationCount { get; private set; }
    }

    private sealed class ApprovalChatClient : IChatClient
    {
        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata)
                ? new ChatClientMetadata("approval-test", null, "approval-test")
                : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "complete")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var materializedMessages = messages.ToList();
            var lastUserIndex = materializedMessages.FindLastIndex(message =>
                message.Role == ChatRole.User &&
                string.Concat(message.Contents.OfType<TextContent>().Select(static content => content.Text)) is "A" or "B" or "Deny");
            var lastFunctionResultIndex = materializedMessages.FindLastIndex(message =>
                message.Contents.OfType<FunctionResultContent>().Any(content =>
                    content.CallId.StartsWith("protected-", StringComparison.Ordinal)));
            if (lastFunctionResultIndex > lastUserIndex)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "complete");
                yield break;
            }

            var lastUser = materializedMessages[lastUserIndex];
            var value = string.Concat(lastUser.Contents.OfType<TextContent>().Select(static content => content.Text));
            if (value is "A" or "B" or "Deny")
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        $"protected-{Guid.NewGuid():N}",
                        "protected_operation",
                        new Dictionary<string, object?> { ["value"] = value })
                ]);
                yield break;
            }

            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "complete");
        }
    }

    private static ChatEngineSessionStartRequest CreateStartRequest() =>
        new()
        {
            Configuration = new AppChatConfiguration("model", []),
            Agents = [],
            RuntimeReference = new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, "agent")
        };

    private static ChatEngineSessionStartRequest CreateSandboxStartRequest() =>
        new()
        {
            Configuration = new AppChatConfiguration("model", []),
            Agents = [],
            RuntimeReference = new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, "agent"),
            Overrides = new AgentSessionOverrides
            {
                WorkspacePath = Environment.CurrentDirectory,
                SandboxProfileId = Guid.NewGuid()
            }
        };

    private static StubDefinitionCatalog CreateSandboxCatalog() =>
        new(new AgentLaunchCapabilities
        {
            SupportsWorkspace = true,
            SupportsSandboxProfile = true
        });

    public static TheoryData<IReadOnlyList<AgentRunEvent>, string> CompletedContentCases()
    {
        var data = new TheoryData<IReadOnlyList<AgentRunEvent>, string>
        {
            {
                [
                    new AgentTextDelta("m1", "Agent", "answer"),
                    new AgentMessageCompleted("m1", new AgentOutputMessage("Agent", "answer"))
                ],
                "answer"
            },
            {
                [
                    new AgentTextDelta("m1", "Agent", "answer "),
                    new AgentMessageCompleted("m1", new AgentOutputMessage("Agent", "answer"))
                ],
                "answer"
            },
            {
                [
                    new AgentTextDelta("m1", "Agent", "partial"),
                    new AgentMessageCompleted("m1", new AgentOutputMessage("Agent", "final answer"))
                ],
                "final answer"
            },
            {
                [
                    new AgentTextDelta("m1", "Agent", "final"),
                    new AgentTextDelta("m1", "Agent", " answer "),
                    new AgentMessageCompleted("m1", new AgentOutputMessage("Agent", "final answer"))
                ],
                "final answer"
            },
            {
                [
                    new AgentMessageCompleted("m1", new AgentOutputMessage("Agent", "answer"))
                ],
                "answer"
            }
        };

        return data;
    }

    private sealed class StubAgentRunner(IReadOnlyList<AgentRunEvent> events) : IAgentRunner
    {
        public AgentRuntimeRunRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<AgentRunEvent> RunAsync(
            AgentDefinitionReference reference,
            AgentRuntimeRunRequest request,
            AgentRuntimeCreationContext creationContext,
            AgentRunContext runContext,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            foreach (var runEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return runEvent;
            }
        }
    }

    private sealed class BlockingAgentRunner : IAgentRunner
    {
        private readonly TaskCompletionSource _streaming =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilStreamingAsync() => _streaming.Task.WaitAsync(TimeSpan.FromSeconds(3));

        public async IAsyncEnumerable<AgentRunEvent> RunAsync(
            AgentDefinitionReference reference,
            AgentRuntimeRunRequest request,
            AgentRuntimeCreationContext creationContext,
            AgentRunContext runContext,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentTextDelta("m1", "Agent", "partial");
            _streaming.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class StubDefinitionCatalog(AgentLaunchCapabilities? launchCapabilities = null) : IAgentDefinitionCatalog
    {
        public Task<IReadOnlyList<AgentDefinitionDescriptor>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentDefinitionDescriptor>>([]);

        public Task<AgentDefinitionDescriptor?> FindAsync(
            AgentDefinitionReference reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentDefinitionDescriptor?>(new AgentDefinitionDescriptor
            {
                Reference = reference,
                Name = "Agent",
                RuntimeKind = reference.Kind == AgentDefinitionKind.SavedWorkflow
                    ? AgentRuntimeKind.WorkflowAgent
                    : AgentRuntimeKind.LlmAgent,
                ModelRequirement = AgentModelRequirement.Required,
                LaunchCapabilities = launchCapabilities ?? new AgentLaunchCapabilities()
            });

        public async Task<AgentDefinitionDescriptor> GetRequiredAsync(
            AgentDefinitionReference reference,
            CancellationToken cancellationToken = default) =>
            await FindAsync(reference, cancellationToken) ?? throw new KeyNotFoundException();
    }

    private sealed class StubSandboxSessionFactory : ISandboxSessionFactory
    {
        public Task<SandboxSessionHandle> StartAsync(
            Guid profileId,
            string workspacePath,
            string sessionId,
            CancellationToken cancellationToken = default,
            IProgress<ChatSessionStartProgress>? progress = null) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingSandboxSessionFactory : ISandboxSessionFactory
    {
        private readonly TaskCompletionSource _called =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public List<string> SessionIds { get; } = [];

        public Task WaitUntilCalledAsync() => _called.Task.WaitAsync(TimeSpan.FromSeconds(3));

        public void Complete() => _release.SetResult();

        public async Task<SandboxSessionHandle> StartAsync(
            Guid profileId,
            string workspacePath,
            string sessionId,
            CancellationToken cancellationToken = default,
            IProgress<ChatSessionStartProgress>? progress = null)
        {
            CallCount++;
            SessionIds.Add(sessionId);
            _called.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return CreateSandboxHandle(profileId, workspacePath);
        }
    }

    private sealed class FailThenSucceedSandboxSessionFactory : ISandboxSessionFactory
    {
        public int CallCount { get; private set; }

        public Task<SandboxSessionHandle> StartAsync(
            Guid profileId,
            string workspacePath,
            string sessionId,
            CancellationToken cancellationToken = default,
            IProgress<ChatSessionStartProgress>? progress = null)
        {
            CallCount++;
            if (CallCount == 1)
            {
                throw new InvalidOperationException("Failed to start container (125): name conflict");
            }

            return Task.FromResult(CreateSandboxHandle(profileId, workspacePath));
        }
    }

    private static SandboxSessionHandle CreateSandboxHandle(Guid profileId, string workspacePath)
    {
        var sandbox = new StubSandbox(workspacePath);
        return new SandboxSessionHandle(() => sandbox.DisposeAsync())
        {
            ProfileId = profileId,
            ProfileName = ".NET 10 Small",
            ProviderType = "docker",
            Summary = new SandboxDefinitionSummary("mcr.microsoft.com/dotnet/sdk:10.0-noble", "test", "none"),
            WorkspacePath = workspacePath,
            Instance = sandbox
        };
    }

    private sealed class StubSandbox(string workspacePath) : ISandbox
    {
        public string ProviderType => "docker";

        public string WorkspacePath => workspacePath;

        public SandboxState State => SandboxState.Running;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SandboxCommandResult> ExecuteAsync(string command, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SandboxCommandResult(string.Empty, string.Empty, 0, false));
    }
}
