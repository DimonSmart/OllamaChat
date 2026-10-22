using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using Moq;
using System.Runtime.CompilerServices;

namespace ChatClient.Tests;

public sealed class HeadlessSavedSkillsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscoveredWorkspaceSkillsRequireFunctionCalling_AndPinProfile(bool functionsAvailable)
    {
        var root = Path.Combine(Path.GetTempPath(), "ollamachat-headless-skills-" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, "skills", "example");
        Directory.CreateDirectory(skillDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"),
                "---\nname: example\ndescription: Example skill\n---\nRead this skill.",
                TestContext.Current.CancellationToken);
            var agentId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var profile = new AgentSkillsProfile
            {
                FileSources = [new SkillFileSource { Directory = "skills", Patterns = ["**/SKILL.md"] }]
            };
            var reference = new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, agentId.ToString("D"));
            var model = new ServerModel(Guid.NewGuid(), "local");
            var agent = new AgentTemplateDefinition
            {
                Id = agentId, AgentName = "Skills only", LlmId = model.ServerId,
                ModelName = model.ModelName, SkillsProviderProfileId = profile.Id
            };
            var descriptor = new AgentDefinitionDescriptor
            {
                Reference = reference, Name = "Skills only", RuntimeKind = AgentRuntimeKind.LlmAgent,
                ModelRequirement = AgentModelRequirement.Required,
                LaunchCapabilities = new AgentLaunchCapabilities { SupportsWorkspace = true }
            };
            var resolved = new ResolvedAgentSessionDefinition
            {
                Descriptor = descriptor, RuntimeReference = reference, DefaultModel = model,
                PresentationParticipant = new ChatRuntimeParticipantDescriptor
                {
                    Id = agentId.ToString("D"), Name = "Skills only",
                    RuntimeKind = AgentRuntimeKind.LlmAgent
                },
                Validation = new AgentDefinitionLaunchValidation { CanLaunch = true }
            };
            var templates = new Mock<IAgentTemplateService>();
            templates.Setup(t => t.GetByIdAsync(agentId)).ReturnsAsync(agent);
            var catalog = new Mock<IAgentDefinitionCatalog>();
            catalog.Setup(c => c.FindAsync(reference, It.IsAny<CancellationToken>())).ReturnsAsync(descriptor);
            var session = new Mock<IAgentSessionDefinitionResolver>();
            session.Setup(s => s.ResolveAsync(reference, It.IsAny<AgentSessionDefinitionRequest>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(resolved);
            var workspaces = new Mock<IWorkspaceResolver>();
            workspaces.Setup(w => w.ResolveAsync(It.IsAny<WorkspaceSelection>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(new ResolvedWorkspace(workspaceId, root));
            var skills = new Mock<IAgentSkillsProfileService>();
            skills.Setup(s => s.GetByIdAsync(profile.Id)).ReturnsAsync(profile);
            var capability = new Mock<IAgentLaunchCapabilityValidator>();
            capability.Setup(c => c.SupportsFunctionCallingAsync(model, It.IsAny<CancellationToken>()))
                .ReturnsAsync(functionsAvailable);
            var runner = new Mock<IAgentRunner>();
            AgentRuntimeCreationContext? seen = null;
            runner.Setup(r => r.RunAsync(reference, It.IsAny<AgentRuntimeRunRequest>(),
                    It.IsAny<AgentRuntimeCreationContext>(), It.IsAny<AgentRunContext>(),
                    It.IsAny<CancellationToken>()))
                .Returns((AgentDefinitionReference _, AgentRuntimeRunRequest _, AgentRuntimeCreationContext creation,
                    AgentRunContext _, CancellationToken _) =>
                {
                    seen = creation;
                    return Finished();
                });
            var executor = new HeadlessAgentExecutor(templates.Object, catalog.Object,
                session.Object, new AgentRunContextFactory(), runner.Object,
                workspaces.Object, null, skills.Object, capability.Object);
            var outcome = await executor.ExecuteAsync(
                new HeadlessAgentRunRequest(agentId, "Load example skill", workspaceId),
                TestContext.Current.CancellationToken);

            if (functionsAvailable)
            {
                Assert.True(outcome.Succeeded);
                Assert.NotNull(seen);
                Assert.Equal(root, seen.RuntimeResources.WorkspacePath);
                Assert.Equal(profile.Id, seen.RuntimeResources.HeadlessSkillsProfile!.Id);
                Assert.NotSame(profile, seen.RuntimeResources.HeadlessSkillsProfile);
            }
            else
            {
                Assert.Equal("agent_preflight_failed", outcome.ErrorCode);
                runner.Verify(r => r.RunAsync(It.IsAny<AgentDefinitionReference>(),
                    It.IsAny<AgentRuntimeRunRequest>(), It.IsAny<AgentRuntimeCreationContext>(),
                    It.IsAny<AgentRunContext>(), It.IsAny<CancellationToken>()), Times.Never);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async IAsyncEnumerable<AgentRunEvent> Finished(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new AgentRunCompleted(new AgentRunResult
        {
            FinalMessage = new AgentOutputMessage("agent", "Skill loaded")
        });
    }
}
