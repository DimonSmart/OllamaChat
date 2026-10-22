using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using Moq;
using System.Runtime.CompilerServices;

namespace ChatClient.Tests;

public sealed class HeadlessSavedWorkspaceTests
{
    [Fact]
    public async Task ReadOnlyWorkspaceIsPinned_AndNeverGrantsWrites()
    {
        var fixture = new Fixture();
        fixture.Profile.AccessMode = FileAccessMode.ReadOnly;
        fixture.Profile.RequireReadApproval = false;
        var creation = default(AgentRuntimeCreationContext);
        fixture.Runner.Setup(r => r.RunAsync(fixture.Reference, It.IsAny<AgentRuntimeRunRequest>(),
                It.IsAny<AgentRuntimeCreationContext>(), It.IsAny<AgentRunContext>(),
                It.IsAny<CancellationToken>()))
            .Returns((AgentDefinitionReference _, AgentRuntimeRunRequest _, AgentRuntimeCreationContext c,
                AgentRunContext _, CancellationToken _) =>
            {
                creation = c;
                return Finished();
            });
        var result = await fixture.Executor.ExecuteAsync(
            new HeadlessAgentRunRequest(fixture.AgentId, "Read README.md", fixture.WorkspaceId),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.NotNull(creation);
        Assert.Equal(fixture.WorkspacePath, creation.RuntimeResources.WorkspacePath);
        Assert.Equal(fixture.WorkspacePath, creation.Overrides.WorkspacePath);
        Assert.True(creation.RuntimeResources.IsHeadlessExecution);
        Assert.Equal(FileAccessMode.ReadOnly, creation.RuntimeResources.HeadlessFileAccessProfile!.AccessMode);
        Assert.False(creation.RuntimeResources.HeadlessFileAccessProfile.RequireReadApproval);
        Assert.NotSame(fixture.Profile, creation.RuntimeResources.HeadlessFileAccessProfile);
        Assert.Null(creation.RuntimeResources.Sandbox);
        Assert.NotNull(creation.RuntimeResources.ToolApprovalCoordinator);

        fixture.Profile.AccessMode = FileAccessMode.ReadWrite;
        Assert.Equal(FileAccessMode.ReadOnly, creation.RuntimeResources.HeadlessFileAccessProfile.AccessMode);
    }

    [Fact]
    public async Task TextOnlyAgentCanUseSavedWorkspaceWithoutGainingFileTools()
    {
        var fixture = new Fixture(withFileAccess: false);
        AgentRuntimeCreationContext? seen = null;
        fixture.Runner.Setup(r => r.RunAsync(fixture.Reference, It.IsAny<AgentRuntimeRunRequest>(),
                It.IsAny<AgentRuntimeCreationContext>(), It.IsAny<AgentRunContext>(),
                It.IsAny<CancellationToken>()))
            .Returns((AgentDefinitionReference _, AgentRuntimeRunRequest _, AgentRuntimeCreationContext c,
                AgentRunContext _, CancellationToken _) =>
            {
                seen = c;
                return Finished();
            });
        var outcome = await fixture.Executor.ExecuteAsync(
            new HeadlessAgentRunRequest(fixture.AgentId, "Hello", fixture.WorkspaceId),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.Equal(fixture.WorkspacePath, seen!.RuntimeResources.WorkspacePath);
        Assert.Null(seen.RuntimeResources.HeadlessFileAccessProfile);
        Assert.Null(seen.RuntimeResources.HeadlessSkillsProfile);
        Assert.Null(seen.RuntimeResources.Sandbox);
    }

    [Fact]
    public async Task HeadlessRejectsWriteOrInteractiveReadApprovalBeforeRunning()
    {
        var fixture = new Fixture();
        fixture.Profile.AccessMode = FileAccessMode.ReadWrite;
        fixture.Profile.RequireReadApproval = false;
        var rejectedWrite = await fixture.Executor.ExecuteAsync(
            new HeadlessAgentRunRequest(fixture.AgentId, "Read", fixture.WorkspaceId),
            TestContext.Current.CancellationToken);
        Assert.Equal("headless_capability_unsupported", rejectedWrite.ErrorCode);

        fixture.Profile.AccessMode = FileAccessMode.ReadOnly;
        fixture.Profile.RequireReadApproval = true;
        var rejectedApproval = await fixture.Executor.ExecuteAsync(
            new HeadlessAgentRunRequest(fixture.AgentId, "Read", fixture.WorkspaceId),
            TestContext.Current.CancellationToken);
        Assert.Equal("interactive_approval_required", rejectedApproval.ErrorCode);
        fixture.Runner.Verify(r => r.RunAsync(It.IsAny<AgentDefinitionReference>(),
            It.IsAny<AgentRuntimeRunRequest>(), It.IsAny<AgentRuntimeCreationContext>(),
            It.IsAny<AgentRunContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnknownSavedWorkspaceFailsWithoutLaunchingRuntime()
    {
        var fixture = new Fixture(missingWorkspace: true);
        var result = await fixture.Executor.ExecuteAsync(
            new HeadlessAgentRunRequest(fixture.AgentId, "Read", fixture.WorkspaceId),
            TestContext.Current.CancellationToken);
        Assert.Equal("workspace_not_found", result.ErrorCode);
    }

    private static async IAsyncEnumerable<AgentRunEvent> Finished(
        [EnumeratorCancellation] CancellationToken token = default)
    {
        await Task.Yield();
        yield return new AgentRunCompleted(new AgentRunResult
        {
            FinalMessage = new AgentOutputMessage("agent", "Done")
        });
    }

    private sealed class Fixture
    {
        public Guid AgentId { get; } = Guid.NewGuid();
        public Guid WorkspaceId { get; } = Guid.NewGuid();
        public string WorkspacePath { get; } = AppContext.BaseDirectory;
        public AgentDefinitionReference Reference { get; }
        public FileAccessProviderProfile Profile { get; } = new() { AccessMode = FileAccessMode.ReadOnly, RequireReadApproval = false };
        public Mock<IAgentRunner> Runner { get; } = new();
        public HeadlessAgentExecutor Executor { get; }

        public Fixture(bool missingWorkspace = false, bool withFileAccess = true)
        {
            Reference = new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, AgentId.ToString("D"));
            var model = new ServerModel(Guid.NewGuid(), "local");
            var template = new AgentTemplateDefinition
            {
                Id = AgentId, AgentName = "Test", LlmId = model.ServerId,
                ModelName = model.ModelName, FileAccessProviderProfileId = withFileAccess ? Profile.Id : null
            };
            var descriptor = new AgentDefinitionDescriptor
            {
                Reference = Reference, Name = "Test", RuntimeKind = AgentRuntimeKind.LlmAgent,
                ModelRequirement = AgentModelRequirement.Required,
                LaunchCapabilities = new AgentLaunchCapabilities
                {
                    SupportsWorkspace = withFileAccess, RequiresFunctionCalling = withFileAccess
                }
            };
            var resolved = new ResolvedAgentSessionDefinition
            {
                Descriptor = descriptor, RuntimeReference = Reference, DefaultModel = model,
                PresentationParticipant = new ChatRuntimeParticipantDescriptor
                {
                    Id = AgentId.ToString("D"), Name = "Test", RuntimeKind = AgentRuntimeKind.LlmAgent
                },
                Validation = new AgentDefinitionLaunchValidation { CanLaunch = true }
            };
            var templates = new Mock<IAgentTemplateService>();
            templates.Setup(t => t.GetByIdAsync(AgentId)).ReturnsAsync(template);
            var catalog = new Mock<IAgentDefinitionCatalog>();
            catalog.Setup(c => c.FindAsync(Reference, It.IsAny<CancellationToken>())).ReturnsAsync(descriptor);
            var session = new Mock<IAgentSessionDefinitionResolver>();
            session.Setup(s => s.ResolveAsync(Reference, It.IsAny<AgentSessionDefinitionRequest>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(resolved);
            var workspaces = new Mock<IWorkspaceResolver>();
            if (missingWorkspace)
                workspaces.Setup(w => w.ResolveAsync(It.IsAny<WorkspaceSelection>(),
                    It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new WorkspaceException("workspace_not_found", "Workspace was removed."));
            else
                workspaces.Setup(w => w.ResolveAsync(It.IsAny<WorkspaceSelection>(),
                    It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new ResolvedWorkspace(WorkspaceId, WorkspacePath));
            var files = new Mock<IFileAccessProviderProfileService>();
            files.Setup(f => f.GetByIdAsync(Profile.Id)).ReturnsAsync(() => Profile);
            Executor = new HeadlessAgentExecutor(templates.Object, catalog.Object, session.Object,
                new AgentRunContextFactory(), Runner.Object, workspaces.Object, files.Object);
        }
    }
}
