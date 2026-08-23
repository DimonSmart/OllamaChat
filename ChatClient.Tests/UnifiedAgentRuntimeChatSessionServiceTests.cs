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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentModeProviderOptions = Microsoft.Agents.AI.AgentModeProviderOptions;
using AgentSession = Microsoft.Agents.AI.AgentSession;
using AIAgent = Microsoft.Agents.AI.AIAgent;
using BackgroundAgentsProvider = Microsoft.Agents.AI.BackgroundAgentsProvider;
using FileMemoryProvider = Microsoft.Agents.AI.FileMemoryProvider;
using FileMemoryState = Microsoft.Agents.AI.FileMemoryState;
using FileSystemAgentFileStore = Microsoft.Agents.AI.FileSystemAgentFileStore;
using HarnessAgentOptions = Microsoft.Agents.AI.HarnessAgentOptions;
using TodoProvider = Microsoft.Agents.AI.TodoProvider;

#pragma warning disable MAAI001

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
    public async Task DirectHarness_FileMemoryWithoutFunctionCalling_FailsBeforeRun()
    {
        var fixture = CreateDirectFixture(enableFileMemory: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.StartAsync(fixture.Request, TestContext.Current.CancellationToken));

        Assert.Contains("function calling required by File Memory", exception.Message);
        Assert.Empty(fixture.ChatClient.Requests);
    }

    [Fact]
    public async Task GetSessionStateAsync_DistinguishesDisabledFromEnabledEmptyFileMemory()
    {
        var fixture = CreateDirectFixture();
        var disabledAgent = new RecordingChatClient().AsHarnessAgent(new HarnessAgentOptions
        {
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableCompaction = true
        });
        var disabledSession = await disabledAgent.CreateSessionAsync(TestContext.Current.CancellationToken);
        InstallDirectHarness(fixture.Service, disabledAgent, disabledSession, []);

        var disabled = await fixture.Service.GetSessionStateAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(disabled?.FileMemory);
        Assert.False(disabled!.FileMemory!.Enabled);
        Assert.Empty(disabled.FileMemory.Files);

        var directory = Path.Combine(Path.GetTempPath(), $"ollamachat-file-memory-{Guid.NewGuid():N}");
        try
        {
            var store = new FileSystemAgentFileStore(directory);
            var enabledAgent = new RecordingChatClient().AsHarnessAgent(new HarnessAgentOptions
            {
                DisableTodoProvider = true,
                DisableAgentModeProvider = true,
                DisableWebSearch = true,
                DisableFileMemory = false,
                FileMemoryStore = store,
                DisableAgentSkillsProvider = true,
                DisableCompaction = true
            });
            var enabledSession = await enabledAgent.CreateSessionAsync(TestContext.Current.CancellationToken);
            InstallDirectHarness(fixture.Service, enabledAgent, enabledSession, []);
            SetPrivateField(fixture.Service, "_directFileMemoryStore", store);

            var enabled = await fixture.Service.GetSessionStateAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(enabled?.FileMemory);
            Assert.True(enabled!.FileMemory!.Enabled);
            Assert.Empty(enabled.FileMemory.Files);

        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("project.md", "project_description.md")]
    [InlineData("project.notes.md", "project.notes_description.md")]
    [InlineData("project", "project_description.md")]
    [InlineData(".hidden", ".hidden_description.md")]
    public void GetDescriptionFileName_MatchesFrameworkSidecarNaming(string memoryFileName, string expected)
    {
        Assert.Equal(expected, UnifiedAgentRuntimeChatSessionService.GetDescriptionFileName(memoryFileName));
    }

    [Fact]
    public async Task FileMemory_PersistsBetweenRunsOfTheSameHarnessSession()
    {
        await using var fixture = new FileMemoryHarnessFixture();
        var session = await fixture.Agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await RunHarnessAsync(fixture.Agent, session);
        var before = GetFileMemoryState(fixture.Agent, session);
        await fixture.Store.WriteAsync($"{before.WorkingFolder}/project.md", "Blue Parrot", TestContext.Current.CancellationToken);

        await RunHarnessAsync(fixture.Agent, session);
        var after = GetFileMemoryState(fixture.Agent, session);

        Assert.Equal(before.WorkingFolder, after.WorkingFolder);
        Assert.True(await fixture.Store.FileExistsAsync($"{after.WorkingFolder}/project.md", TestContext.Current.CancellationToken));
        Assert.Equal("Blue Parrot", await fixture.Store.ReadAsync($"{after.WorkingFolder}/project.md", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FileMemory_IsolatedBetweenHarnessSessions()
    {
        await using var fixture = new FileMemoryHarnessFixture();
        var sessionA = await fixture.Agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var sessionB = await fixture.Agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        await RunHarnessAsync(fixture.Agent, sessionA);
        await RunHarnessAsync(fixture.Agent, sessionB);
        var stateA = GetFileMemoryState(fixture.Agent, sessionA);
        var stateB = GetFileMemoryState(fixture.Agent, sessionB);
        await fixture.Store.WriteAsync($"{stateA.WorkingFolder}/a.md", "A", TestContext.Current.CancellationToken);

        Assert.NotEqual(stateA.WorkingFolder, stateB.WorkingFolder);
        Assert.True(await fixture.Store.FileExistsAsync($"{stateA.WorkingFolder}/a.md", TestContext.Current.CancellationToken));
        Assert.False(await fixture.Store.FileExistsAsync($"{stateB.WorkingFolder}/a.md", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetSessionStateAsync_ProjectsDescriptionsAndHidesFrameworkInternalMemoryFiles()
    {
        await using var harness = new FileMemoryHarnessFixture();
        var session = await harness.Agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await RunHarnessAsync(harness.Agent, session);
        var memory = GetFileMemoryState(harness.Agent, session);
        await harness.Store.WriteAsync($"{memory.WorkingFolder}/project.md", "Blue Parrot", TestContext.Current.CancellationToken);
        await harness.Store.WriteAsync($"{memory.WorkingFolder}/project_description.md", "Project information", TestContext.Current.CancellationToken);

        var fixture = CreateDirectFixture();
        InstallDirectHarness(fixture.Service, harness.Agent, session, []);
        SetPrivateField(fixture.Service, "_directFileMemoryStore", harness.Store);

        var state = await fixture.Service.GetSessionStateAsync(TestContext.Current.CancellationToken);

        var fileMemory = Assert.IsType<AgentSessionFileMemoryViewModel>(state!.FileMemory);
        Assert.True(fileMemory.Enabled);
        var entry = Assert.Single(fileMemory.Files);
        Assert.Equal("project.md", entry.Name);
        Assert.Equal("Project information", entry.Description);
        Assert.DoesNotContain(fileMemory.Files, file => file.Name is "memories.md" or "project_description.md");
    }

    [Fact]
    public async Task ClearFileMemoryAsync_ClearsOnlyTheActiveHarnessSession()
    {
        await using var harness = new FileMemoryHarnessFixture();
        var sessionA = await harness.Agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var sessionB = await harness.Agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await RunHarnessAsync(harness.Agent, sessionA);
        await RunHarnessAsync(harness.Agent, sessionB);
        var stateA = GetFileMemoryState(harness.Agent, sessionA);
        var stateB = GetFileMemoryState(harness.Agent, sessionB);
        await harness.Store.WriteAsync($"{stateA.WorkingFolder}/a.md", "A", TestContext.Current.CancellationToken);
        await harness.Store.WriteAsync($"{stateA.WorkingFolder}/a_description.md", "A description", TestContext.Current.CancellationToken);
        await harness.Store.WriteAsync($"{stateB.WorkingFolder}/b.md", "B", TestContext.Current.CancellationToken);

        var fixture = CreateDirectFixture();
        InstallDirectHarness(fixture.Service, harness.Agent, sessionA, []);
        SetPrivateField(fixture.Service, "_directFileMemoryStore", harness.Store);
        await fixture.Service.ClearFileMemoryAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await harness.Store.ListChildrenAsync(stateA.WorkingFolder, TestContext.Current.CancellationToken));
        Assert.True(await harness.Store.FileExistsAsync($"{stateB.WorkingFolder}/b.md", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FileMemory_RunCompletionRaisesStateChangedAndRefreshesProjection()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ollamachat-file-memory-{Guid.NewGuid():N}");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentFileMemory:RootPath"] = directory })
                .Build();
            var fixture = CreateDirectFixture(enableFileMemory: true, supportsFunctionCalling: true, configuration: configuration);
            var stateChanged = 0;
            fixture.Service.SessionStateChanged += () => stateChanged++;

            await fixture.Service.StartAsync(fixture.Request, TestContext.Current.CancellationToken);
            await fixture.Service.SendAsync("first", cancellationToken: TestContext.Current.CancellationToken);
            var initial = await fixture.Service.GetSessionStateAsync(TestContext.Current.CancellationToken);
            var workingFolder = Assert.IsType<AgentSessionFileMemoryViewModel>(initial!.FileMemory).WorkingFolder!;
            var store = GetPrivateField<Microsoft.Agents.AI.AgentFileStore>(fixture.Service, "_directFileMemoryStore");
            await store.WriteAsync($"{workingFolder}/project.md", "Blue Parrot", TestContext.Current.CancellationToken);
            stateChanged = 0;

            await fixture.Service.SendAsync("second", cancellationToken: TestContext.Current.CancellationToken);
            var refreshed = await fixture.Service.GetSessionStateAsync(TestContext.Current.CancellationToken);

            Assert.True(stateChanged > 0);
            Assert.Contains(refreshed!.FileMemory!.Files, file => file.Name == "project.md");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void HaveEquivalentWorkspacePaths_UsesOsAwareNormalizedComparison()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "HarnessWorkspace");

        Assert.True(UnifiedAgentRuntimeChatSessionService.HaveEquivalentWorkspacePaths(workspace, workspace + Path.DirectorySeparatorChar));
        Assert.Equal(OperatingSystem.IsWindows(), UnifiedAgentRuntimeChatSessionService.HaveEquivalentWorkspacePaths(workspace, workspace.ToLowerInvariant()));
        Assert.False(UnifiedAgentRuntimeChatSessionService.HaveEquivalentWorkspacePaths(workspace, Path.Combine(Path.GetTempPath(), "OtherHarnessWorkspace")));
        Assert.False(UnifiedAgentRuntimeChatSessionService.HaveEquivalentWorkspacePaths(null, workspace));
        Assert.True(UnifiedAgentRuntimeChatSessionService.HaveEquivalentWorkspacePaths(null, null));
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
    public async Task HarnessSession_RestorePreservesCurrentSavedAgentConfigurationForTheNextTurn()
    {
        var fixture = CreateDirectFixture();
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        var originalAgent = Assert.Single(fixture.Service.Agents);

        await fixture.Service.SendAsync("before export", cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = await fixture.Service.ExportHarnessSessionAsync(TestContext.Current.CancellationToken);

        await fixture.Service.RestoreHarnessSessionAsync(snapshot, TestContext.Current.CancellationToken);

        var restoredAgent = Assert.Single(fixture.Service.Agents);
        Assert.Equal(originalAgent.AgentId, restoredAgent.AgentId);
        Assert.Equal("Test Agent", restoredAgent.AgentName);

        await fixture.Service.SendAsync("after restore", cancellationToken: TestContext.Current.CancellationToken);

        var request = fixture.ChatClient.Requests[^1];
        Assert.Equal("test-model", request.Options?.ModelId);
        Assert.Equal(0.35f, request.Options?.Temperature);
        Assert.Equal(1.15, request.Options?.AdditionalProperties?["repeat_penalty"]);
        Assert.Equal("Test Agent", fixture.Service.Messages.Last().AgentName);
    }

    [Fact]
    public async Task HarnessSession_RestorePreservesAgentMode()
    {
        var fixture = CreateDirectFixture(availableModes: ["Plan", "Execute"]);
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        await fixture.Service.SetAgentModeAsync("Execute", cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = await fixture.Service.ExportHarnessSessionAsync(TestContext.Current.CancellationToken);

        await fixture.Service.RestoreHarnessSessionAsync(snapshot, TestContext.Current.CancellationToken);

        Assert.Equal("Execute", (await fixture.Service.GetSessionStateAsync(TestContext.Current.CancellationToken))!.Mode);
    }

    [Fact]
    public async Task RestoreHarnessSessionAsync_DistinguishesNullAndEmptyMcpParameterValues()
    {
        var fixture = CreateDirectFixture(mcpBindings:
        [
            new McpServerSessionBinding
            {
                ServerName = "github",
                Parameters = new Dictionary<string, string?> { ["token"] = null }
            }
        ]);
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = await fixture.Service.ExportHarnessSessionAsync(TestContext.Current.CancellationToken);
        var node = SnapshotNode(snapshot);
        node["Overrides"]!["McpServerBindings"]!.AsArray()[0]!["Parameters"]!["token"] = string.Empty;

        await AssertRestoreRejectedAndKeepsSessionUsableAsync(fixture, node.ToJsonString(), "different MCP bindings");
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("selected tools")]
    [InlineData("roots")]
    [InlineData("parameters")]
    public async Task RestoreHarnessSessionAsync_RejectsMalformedMcpBindingsWithoutReplacingTheCurrentSession(string malformedPart)
    {
        var fixture = CreateDirectFixture(mcpBindings: [new McpServerSessionBinding { ServerName = "github" }]);
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        var node = SnapshotNode(await fixture.Service.ExportHarnessSessionAsync(TestContext.Current.CancellationToken));
        var bindings = node["Overrides"]!["McpServerBindings"]!.AsArray();
        switch (malformedPart)
        {
            case "binding":
                bindings[0] = null;
                break;
            case "selected tools":
                bindings[0]!["SelectedTools"] = null;
                break;
            case "roots":
                bindings[0]!["Roots"] = null;
                break;
            case "parameters":
                bindings[0]!["Parameters"] = null;
                break;
        }

        await AssertRestoreRejectedAndKeepsSessionUsableAsync(fixture, node.ToJsonString(), "not a valid Harness session snapshot");
    }

    [Theory]
    [MemberData(nameof(IncompatibleSnapshotMutations))]
    public async Task RestoreHarnessSessionAsync_RejectsIncompatibleSnapshotsWithoutReplacingTheCurrentSession(
        string _,
        Action<JsonObject> mutate)
    {
        var fixture = CreateDirectFixture();
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = await fixture.Service.ExportHarnessSessionAsync(TestContext.Current.CancellationToken);
        var node = SnapshotNode(snapshot);
        mutate(node);

        await AssertRestoreRejectedAndKeepsSessionUsableAsync(fixture, node.ToJsonString());
    }

    public static IEnumerable<object[]> IncompatibleSnapshotMutations()
    {
        yield return ["saved agent", (Action<JsonObject>)(node => node["SavedAgentId"] = Guid.NewGuid().ToString())];
        yield return ["model server", (Action<JsonObject>)(node => node["ModelServerId"] = Guid.NewGuid().ToString())];
        yield return ["model", (Action<JsonObject>)(node => node["ModelName"] = "other-model")];
        yield return ["agent configuration", (Action<JsonObject>)(node => node["AgentUpdatedAt"] = DateTime.UtcNow.AddDays(-1))];
        yield return ["workspace", (Action<JsonObject>)(node => node["Overrides"]!["WorkspacePath"] = Path.Combine(Path.GetTempPath(), "different-workspace"))];
        yield return ["sandbox", (Action<JsonObject>)(node => node["Overrides"]!["SandboxProfileId"] = Guid.NewGuid().ToString())];
        yield return ["format", (Action<JsonObject>)(node => node["FormatVersion"] = 99)];
        yield return ["missing overrides", (Action<JsonObject>)(node => node["Overrides"] = null)];
        yield return ["absent overrides", (Action<JsonObject>)(node => node.Remove("Overrides"))];
        yield return ["missing session", (Action<JsonObject>)(node => node.Remove("Session"))];
        yield return ["null session", (Action<JsonObject>)(node => node["Session"] = null)];
    }

    [Fact]
    public async Task RestoreHarnessSessionAsync_RejectsInvalidJsonWithoutReplacingTheCurrentSession()
    {
        var fixture = CreateDirectFixture();
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);

        await AssertRestoreRejectedAndKeepsSessionUsableAsync(fixture, "not json");
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
    public async Task RestoreHarnessSessionAsync_WhenDeserializationFails_KeepsTheCurrentSessionUsable()
    {
        var fixture = CreateDirectFixture();
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        await fixture.Service.SendAsync("before export", cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = await fixture.Service.ExportHarnessSessionAsync(TestContext.Current.CancellationToken);
        var originalAgent = GetPrivateField<AIAgent>(fixture.Service, "_directAgent");
        var invalidSnapshot = WithSession(snapshot, "true");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.RestoreHarnessSessionAsync(invalidSnapshot, TestContext.Current.CancellationToken));

        Assert.Same(originalAgent, GetPrivateField<AIAgent>(fixture.Service, "_directAgent"));
        await fixture.Service.SendAsync("still active", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("still active", CurrentUserText(fixture.ChatClient.Requests[^1].Messages));
    }

    [Fact]
    public async Task RestoreSavedChatAsync_WhenNativeDeserializationFails_KeepsCurrentChatUsable()
    {
        var fixture = CreateDirectFixture();
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        await fixture.Service.SendAsync("active chat", cancellationToken: TestContext.Current.CancellationToken);
        var activeChatId = fixture.Service.Id;
        var activeAgent = GetPrivateField<AIAgent>(fixture.Service, "_directAgent");
        var snapshot = await fixture.Service.ExportHarnessSessionAsync(TestContext.Current.CancellationToken);

        var saved = CreateSavedChat(fixture, WithSession(snapshot, "true"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.RestoreSavedChatAsync(saved, TestContext.Current.CancellationToken));

        Assert.Equal(activeChatId, fixture.Service.Id);
        Assert.Same(activeAgent, GetPrivateField<AIAgent>(fixture.Service, "_directAgent"));
        Assert.Equal("active chat", fixture.Service.Messages.First().Content);
        await fixture.Service.SendAsync("still active", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("still active", CurrentUserText(fixture.ChatClient.Requests[^1].Messages));
    }

    [Fact]
    public async Task RestoreSavedChatAsync_RestoresExactTranscriptWithoutManualRestoreMessage()
    {
        var fixture = CreateDirectFixture();
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        await fixture.Service.SendAsync("Q1", cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = await fixture.Service.ExportHarnessSessionAsync(TestContext.Current.CancellationToken);
        var saved = CreateSavedChat(fixture, snapshot);

        await fixture.Service.RestoreSavedChatAsync(saved, TestContext.Current.CancellationToken);

        Assert.Equal(saved.Messages.Select(static message => message.Content), fixture.Service.Messages.Select(static message => message.Content));
        Assert.DoesNotContain(fixture.Service.Messages, message => message.Content.Contains("Harness session restored", StringComparison.Ordinal));
        await fixture.Service.SendAsync("Q2", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Q2", CurrentUserText(fixture.ChatClient.Requests[^1].Messages));
        Assert.Contains(fixture.ChatClient.Requests[^1].Messages, message => message.Role == ChatRole.User && message.Text == "Q1");
    }

    [Fact]
    public async Task RestoreSavedChatAsync_RejectsSavedAgentWithoutNativeSessionBeforeMutation()
    {
        var fixture = CreateDirectFixture();
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        var activeChatId = fixture.Service.Id;
        var activeAgent = GetPrivateField<AIAgent>(fixture.Service, "_directAgent");

        var saved = CreateSavedChat(fixture, null);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.RestoreSavedChatAsync(saved, TestContext.Current.CancellationToken));

        Assert.Equal(activeChatId, fixture.Service.Id);
        Assert.Same(activeAgent, GetPrivateField<AIAgent>(fixture.Service, "_directAgent"));
    }

    [Fact]
    public async Task CheckpointAfterRootChange_UsesOpenedChatStorageRootAndPersistentId()
    {
        var savedChats = new Mock<ISavedChatService>(MockBehavior.Strict);
        var titleGenerator = new Mock<IChatTitleGenerator>(MockBehavior.Strict);
        titleGenerator.Setup(generator => generator.Generate(It.IsAny<string>())).Returns("Generated");
        savedChats.Setup(service => service.IsAutoSaveEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        SavedChatDocument? checkpoint = null;
        savedChats.Setup(service => service.GetAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Guid _, CancellationToken _) => null);
        savedChats.Setup(service => service.SaveCheckpointAsync(It.IsAny<SavedChatDocument>(), It.IsAny<CancellationToken>()))
            .Callback<SavedChatDocument, CancellationToken>((document, _) => checkpoint = document)
            .Returns(Task.CompletedTask);
        var fixture = CreateDirectFixture(savedChatService: savedChats.Object, chatTitleGenerator: titleGenerator.Object);
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = await fixture.Service.ExportHarnessSessionAsync(TestContext.Current.CancellationToken);
        var rootA = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var saved = CreateSavedChat(fixture, snapshot);
        saved.StorageRoot = rootA;

        await fixture.Service.RestoreSavedChatAsync(saved, TestContext.Current.CancellationToken);
        await fixture.Service.SendAsync("continued", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(checkpoint);
        Assert.Equal(saved.Id, checkpoint.Id);
        Assert.Equal(Path.GetFullPath(rootA), checkpoint.StorageRoot);
    }

    [Fact]
    public async Task RestoreHarnessSessionAsync_DoesNotPersistSessionApprovalGrants()
    {
        var fixture = CreateDirectFixture();
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        var policy = GetPrivateField<SessionToolApprovalPolicy>(fixture.Service, "_toolApprovalPolicy");
        policy.Grant("protected_operation", "test-agent");
        Assert.True(policy.IsApproved("protected_operation", "test-agent"));
        var snapshot = await fixture.Service.ExportHarnessSessionAsync(TestContext.Current.CancellationToken);

        await fixture.Service.RestoreHarnessSessionAsync(snapshot, TestContext.Current.CancellationToken);

        var restoredPolicy = GetPrivateField<SessionToolApprovalPolicy>(fixture.Service, "_toolApprovalPolicy");
        Assert.False(restoredPolicy.IsApproved("protected_operation", "test-agent"));
    }

    [Fact]
    public async Task RestoreHarnessSessionAsync_WhenPreviousSandboxCleanupFails_KeepsRestoredSessionUsable()
    {
        var sandboxes = new DisposeFailingThenSucceedingSandboxSessionFactory();
        var fixture = CreateDirectFixture(sandboxSessionFactory: sandboxes, supportsSandbox: true);
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = await fixture.Service.ExportHarnessSessionAsync(TestContext.Current.CancellationToken);

        await fixture.Service.RestoreHarnessSessionAsync(snapshot, TestContext.Current.CancellationToken);
        await fixture.Service.SendAsync("after sandbox cleanup failure", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, sandboxes.StartCount);
        Assert.Equal("after sandbox cleanup failure", CurrentUserText(fixture.ChatClient.Requests[^1].Messages));
    }

    [Fact]
    public async Task HarnessSession_IncompleteBackgroundTaskRejectsExportAndRestoreWithoutReplacingCurrentResources()
    {
        var sandboxes = new TrackingSandboxSessionFactory();
        var fixture = CreateDirectFixture(sandboxSessionFactory: sandboxes, supportsSandbox: true);
        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = await fixture.Service.ExportHarnessSessionAsync(TestContext.Current.CancellationToken);
        var currentRuntime = GetPrivateField<HarnessAgentRuntimeDefinition>(fixture.Service, "_directRuntimeDefinition");
        var currentSandbox = Assert.Single(sandboxes.Sandboxes);
        var backgroundTask = new BackgroundTaskHarnessFixture();

        try
        {
            await backgroundTask.StartTaskAsync(TestContext.Current.CancellationToken);
#pragma warning disable MAAI001
            var provider = backgroundTask.Agent.GetService<BackgroundAgentsProvider>();
            Assert.NotNull(provider);
            Assert.NotEmpty(provider.GetIncompleteTasks(backgroundTask.Session));
#pragma warning restore MAAI001
            InstallDirectHarness(fixture.Service, backgroundTask.Agent, backgroundTask.Session, []);

            var exportException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Service.ExportHarnessSessionAsync(TestContext.Current.CancellationToken));
            var restoreException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Service.RestoreHarnessSessionAsync(snapshot, TestContext.Current.CancellationToken));

            Assert.Contains("Background Agents are still running", exportException.Message);
            Assert.Contains("Background Agents are still running", restoreException.Message);
            Assert.Same(backgroundTask.Agent, GetPrivateField<AIAgent>(fixture.Service, "_directAgent"));
            Assert.Same(backgroundTask.Session, GetPrivateField<AgentSession>(fixture.Service, "_directSession"));
            Assert.Same(currentRuntime, GetPrivateField<HarnessAgentRuntimeDefinition>(fixture.Service, "_directRuntimeDefinition"));
            Assert.Equal(0, fixture.ChatClient.DisposeCount);
            Assert.Equal(0, currentSandbox.DisposeCount);
            Assert.Equal(1, sandboxes.StartCount);

            await fixture.Service.SendAsync("still active", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("still active", CurrentUserText(backgroundTask.ParentClient.Requests[^1].Messages));
        }
        finally
        {
            backgroundTask.Complete();
        }
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
    public async Task GetSessionStateAsync_ShowsDisabledFileMemoryForDirectAgentWithoutProviders()
    {
        var fixture = CreateDirectFixture();

        await fixture.Service.StartAsync(fixture.Request, cancellationToken: TestContext.Current.CancellationToken);

        var state = await fixture.Service.GetSessionStateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(state!.FileMemory!.Enabled);
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

    private static async Task RunHarnessAsync(AIAgent agent, AgentSession session)
    {
        await foreach (var _ in agent.RunStreamingAsync(
                           [new ChatMessage(ChatRole.User, "Initialize file memory.")],
                           session,
                           cancellationToken: TestContext.Current.CancellationToken))
        {
        }
    }

    private static FileMemoryState GetFileMemoryState(AIAgent agent, AgentSession session)
    {
        var provider = Assert.IsType<FileMemoryProvider>(agent.GetService<FileMemoryProvider>());
        var stateKey = Assert.Single(provider.StateKeys);
        Assert.True(session.StateBag.TryGetValue(stateKey, out FileMemoryState? state));
        return Assert.IsType<FileMemoryState>(state);
    }

    private static DirectFixture CreateDirectFixture(
        bool withSessionStateProviders = false,
        IReadOnlyList<string>? availableModes = null,
        IReadOnlyList<McpServerSessionBinding>? mcpBindings = null,
        ISandboxSessionFactory? sandboxSessionFactory = null,
        bool supportsSandbox = false,
        bool enableFileMemory = false,
        bool supportsFunctionCalling = false,
        IConfiguration? configuration = null,
        ISavedChatService? savedChatService = null,
        IChatTitleGenerator? chatTitleGenerator = null)
    {
        var templateId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var template = new AgentTemplateDefinition
        {
            Id = templateId,
            AgentName = "Test Agent",
            Content = "Answer deterministically.",
            Temperature = 0.35,
            RepeatPenalty = 1.15,
            EnableFileMemory = enableFileMemory
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
            .ReturnsAsync(supportsFunctionCalling);
        var tools = new Mock<IAppToolCatalog>(MockBehavior.Strict);
        tools.Setup(catalog => catalog.ListToolsAsync(It.IsAny<McpClientRequestContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
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
            NullLoggerFactory.Instance,
            configuration: configuration);
        var resolver = new Mock<IAgentSessionDefinitionResolver>(MockBehavior.Strict);
        var descriptor = new AgentDefinitionDescriptor
        {
            Reference = new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, templateId.ToString()),
            Name = template.AgentName,
            RuntimeKind = AgentRuntimeKind.LlmAgent,
            ModelRequirement = AgentModelRequirement.Required
        };
        var launchValidation = new AgentDefinitionLaunchValidation { CanLaunch = true };
        resolver.Setup(value => value.ValidateAsync(It.IsAny<AgentDefinitionReference>(), It.IsAny<AgentSessionDefinitionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(launchValidation);
        resolver.Setup(value => value.ResolveAsync(It.IsAny<AgentDefinitionReference>(), It.IsAny<AgentSessionDefinitionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedAgentSessionDefinition
            {
                Descriptor = descriptor,
                RuntimeReference = descriptor.Reference,
                DefaultModel = model,
                PresentationParticipant = new ChatRuntimeParticipantDescriptor
                {
                    Id = template.AgentId,
                    Name = template.AgentName,
                    RuntimeKind = AgentRuntimeKind.LlmAgent
                },
                Validation = launchValidation
            });
        var service = new UnifiedAgentRuntimeChatSessionService(
            new StubAgentRunner([]),
            new StubDefinitionCatalog(supportsSandbox ? new AgentLaunchCapabilities { SupportsSandboxProfile = true } : null),
            new AgentRunContextFactory(),
            new AgenticChatEngineStreamingBridge(),
            NullLogger<UnifiedAgentRuntimeChatSessionService>.Instance,
            templateService.Object,
            sandboxSessionFactory ?? new StubSandboxSessionFactory(),
            runtimeFactory,
            new HarnessResponseEventProjector(NullLogger<HarnessResponseEventProjector>.Instance),
            savedChatService,
            chatTitleGenerator,
            definitionResolver: resolver.Object);
        var request = new ChatEngineSessionStartRequest
        {
            Configuration = new AppChatConfiguration("test-model", []),
            Agents = [new ResolvedChatAgent(AgentExecutionSpecFactory.FromTemplate(template, model), model)],
            RuntimeReference = new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, templateId.ToString()),
            RuntimeDefaultModel = model,
            Overrides = new AgentSessionOverrides
            {
                McpServerBindings = mcpBindings,
                WorkspacePath = supportsSandbox ? Environment.CurrentDirectory : null,
                SandboxProfileId = supportsSandbox ? Guid.NewGuid() : null
            }
        };

        return new DirectFixture(service, request, chatClient);
    }

    private static string CurrentUserText(IReadOnlyList<ChatMessage> messages) =>
        string.Concat(messages.Last(static message => message.Role == ChatRole.User)
            .Contents.OfType<TextContent>().Select(static content => content.Text));

    private static JsonObject SnapshotNode(string snapshotJson) =>
        JsonNode.Parse(snapshotJson)!.AsObject();

    private static async Task AssertRestoreRejectedAndKeepsSessionUsableAsync(
        DirectFixture fixture,
        string snapshotJson,
        string? expectedMessage = null)
    {
        var originalAgent = GetPrivateField<AIAgent>(fixture.Service, "_directAgent");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.RestoreHarnessSessionAsync(snapshotJson, TestContext.Current.CancellationToken));
        if (expectedMessage is not null)
            Assert.Contains(expectedMessage, exception.Message);

        Assert.Same(originalAgent, GetPrivateField<AIAgent>(fixture.Service, "_directAgent"));
        await fixture.Service.SendAsync("still active", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("still active", CurrentUserText(fixture.ChatClient.Requests[^1].Messages));
    }

    private static string WithSession(string snapshotJson, string sessionJson)
    {
        var snapshot = JsonSerializer.Deserialize<HarnessSessionSnapshot>(snapshotJson)!;
        using var session = JsonDocument.Parse(sessionJson);
        return JsonSerializer.Serialize(new HarnessSessionSnapshot
        {
            SavedAgentId = snapshot.SavedAgentId,
            AgentName = snapshot.AgentName,
            AgentUpdatedAt = snapshot.AgentUpdatedAt,
            ModelServerId = snapshot.ModelServerId,
            ModelName = snapshot.ModelName,
            CreatedAtUtc = snapshot.CreatedAtUtc,
            Overrides = snapshot.Overrides,
            Session = session.RootElement.Clone()
        });
    }

    private static SavedChatDocument CreateSavedChat(DirectFixture fixture, string? snapshot) => new()
    {
        Id = Guid.NewGuid(),
        StorageRoot = Path.GetTempPath(),
        Title = "Saved",
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
        Launch = new SavedChatLaunchSnapshot
        {
            RuntimeReference = new SavedChatRuntimeReference(
                fixture.Request.RuntimeReference!.Kind.ToString(), fixture.Request.RuntimeReference.Id),
            Model = fixture.Request.RuntimeDefaultModel,
            Overrides = new SavedChatOverrides()
        },
        Messages = fixture.Service.Messages.Select(static message => new AppChatMessage(message)).ToList(),
        NativeSession = snapshot is null ? null : new SavedChatNativeSession { SnapshotJson = snapshot }
    };

    private static T GetPrivateField<T>(object instance, string name) where T : class =>
        Assert.IsAssignableFrom<T>(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance));

    private static void SetPrivateField(object instance, string name, object? value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private sealed record DirectFixture(
        UnifiedAgentRuntimeChatSessionService Service,
        ChatEngineSessionStartRequest Request,
        RecordingChatClient ChatClient);

#pragma warning disable MAAI001
    private sealed class FileMemoryHarnessFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ollamachat-file-memory-{Guid.NewGuid():N}");

        public FileMemoryHarnessFixture()
        {
            Store = new FileSystemAgentFileStore(_directory);
            Agent = new RecordingChatClient().AsHarnessAgent(new HarnessAgentOptions
            {
                DisableTodoProvider = true,
                DisableAgentModeProvider = true,
                DisableWebSearch = true,
                DisableFileMemory = false,
                FileMemoryStore = Store,
                DisableAgentSkillsProvider = true,
                DisableCompaction = true
            });
        }
        public AIAgent Agent { get; }

        public FileSystemAgentFileStore Store { get; }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);

            return ValueTask.CompletedTask;
        }
    }
#pragma warning restore MAAI001

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

        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
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

    private sealed class BackgroundTaskHarnessFixture
    {
        private readonly BlockingBackgroundTaskChatClient _childClient = new();

#pragma warning disable MAAI001
        public BackgroundTaskHarnessFixture()
        {
            var child = _childClient.AsHarnessAgent(new HarnessAgentOptions
            {
                Name = "Worker",
                DisableTodoProvider = true,
                DisableAgentModeProvider = true,
                DisableWebSearch = true,
                DisableFileMemory = true,
                DisableAgentSkillsProvider = true,
                DisableCompaction = true
            });
            ParentClient = new BackgroundTaskParentChatClient();
            Agent = ParentClient.AsHarnessAgent(new HarnessAgentOptions
            {
                Name = "Parent",
                BackgroundAgents = [child],
                DisableTodoProvider = true,
                DisableAgentModeProvider = true,
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

        public BackgroundTaskParentChatClient ParentClient { get; }

        public void Complete() => _childClient.Complete();

        public async Task StartTaskAsync(CancellationToken cancellationToken)
        {
            await foreach (var _ in Agent.RunStreamingAsync(
                               [new ChatMessage(ChatRole.User, "start background work")],
                               Session,
                               cancellationToken: cancellationToken))
            {
            }
        }
    }

    private sealed class BackgroundTaskParentChatClient : IChatClient
    {
        public List<RecordedChatRequest> Requests { get; } = [];

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata)
                ? new ChatClientMetadata("background-parent", null, "background-parent")
                : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "started")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var materializedMessages = messages.Select(static message => message.Clone()).ToList();
            Requests.Add(new RecordedChatRequest(materializedMessages, options));
            if (!materializedMessages.SelectMany(static message => message.Contents).OfType<FunctionResultContent>().Any())
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "background-task",
                        "background_agents_start_task",
                        new Dictionary<string, object?>
                        {
                            ["agentName"] = "Worker",
                            ["input"] = "work",
                            ["description"] = "long-running work"
                        })
                ]);
                yield break;
            }

            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "started");
        }
    }

    private sealed class BlockingBackgroundTaskChatClient : IChatClient
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete() => _completion.TrySetResult();

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata)
                ? new ChatClientMetadata("background-child", null, "background-child")
                : null;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await _completion.Task.WaitAsync(cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "completed"));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await _completion.Task.WaitAsync(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "completed");
        }
    }

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

    private sealed class DisposeFailingThenSucceedingSandboxSessionFactory : ISandboxSessionFactory
    {
        public int StartCount { get; private set; }

        public Task<SandboxSessionHandle> StartAsync(
            Guid profileId,
            string workspacePath,
            string sessionId,
            CancellationToken cancellationToken = default,
            IProgress<ChatSessionStartProgress>? progress = null)
        {
            StartCount++;
            var failOnDispose = StartCount == 1;
            var sandbox = new StubSandbox(workspacePath);
            return Task.FromResult(new SandboxSessionHandle(() =>
            {
                if (failOnDispose)
                    throw new InvalidOperationException("Expected old sandbox cleanup failure.");
                return ValueTask.CompletedTask;
            })
            {
                ProfileId = profileId,
                ProfileName = "Test sandbox",
                ProviderType = "test",
                Summary = new SandboxDefinitionSummary("test", "test", "none"),
                WorkspacePath = workspacePath,
                Instance = sandbox
            });
        }
    }

    private sealed class TrackingSandboxSessionFactory : ISandboxSessionFactory
    {
        public int StartCount { get; private set; }

        public List<TrackingSandbox> Sandboxes { get; } = [];

        public Task<SandboxSessionHandle> StartAsync(
            Guid profileId,
            string workspacePath,
            string sessionId,
            CancellationToken cancellationToken = default,
            IProgress<ChatSessionStartProgress>? progress = null)
        {
            StartCount++;
            var sandbox = new TrackingSandbox(workspacePath);
            Sandboxes.Add(sandbox);
            return Task.FromResult(new SandboxSessionHandle(sandbox.DisposeAsync)
            {
                ProfileId = profileId,
                ProfileName = "Test sandbox",
                ProviderType = "test",
                Summary = new SandboxDefinitionSummary("test", "test", "none"),
                WorkspacePath = workspacePath,
                Instance = sandbox
            });
        }
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

    private sealed class TrackingSandbox(string workspacePath) : ISandbox
    {
        public int DisposeCount { get; private set; }

        public string ProviderType => "test";

        public string WorkspacePath => workspacePath;

        public SandboxState State => SandboxState.Running;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SandboxCommandResult> ExecuteAsync(string command, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SandboxCommandResult(string.Empty, string.Empty, 0, false));
    }
}

#pragma warning restore MAAI001
