using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using Moq;
using System.Runtime.CompilerServices;

namespace ChatClient.Tests;

public sealed class HeadlessAgentExecutorTests
{
    [Fact]
    public async Task Executor_UsesFreshRootAndOneUserMessage_AndTerminalTextOnly()
    {
        var id = Guid.NewGuid();
        var reference = new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, id.ToString("D"));
        var template = new AgentTemplateDefinition { Id = id, AgentName = "Test", LlmId = Guid.NewGuid(), ModelName = "local" };
        var descriptor = new AgentDefinitionDescriptor
        {
            Reference = reference, Name = "Test", RuntimeKind = AgentRuntimeKind.LlmAgent,
            ModelRequirement = AgentModelRequirement.Required
        };
        var model = new ServerModel(template.LlmId!.Value, "local");
        var resolved = new ResolvedAgentSessionDefinition
        {
            Descriptor = descriptor, RuntimeReference = reference, DefaultModel = model,
            PresentationParticipant = new ChatRuntimeParticipantDescriptor
            {
                Id = id.ToString("D"), Name = "Test", RuntimeKind = AgentRuntimeKind.LlmAgent
            },
            Validation = new AgentDefinitionLaunchValidation { CanLaunch = true }
        };
        var templates = new Mock<IAgentTemplateService>();
        templates.Setup(t => t.GetByIdAsync(id)).ReturnsAsync(template);
        var catalog = new Mock<IAgentDefinitionCatalog>();
        catalog.Setup(c => c.FindAsync(reference, It.IsAny<CancellationToken>())).ReturnsAsync(descriptor);
        var resolver = new Mock<IAgentSessionDefinitionResolver>();
        resolver.Setup(r => r.ResolveAsync(reference, It.IsAny<AgentSessionDefinitionRequest>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(resolved);
        AgentRuntimeRunRequest? seenRequest = null;
        AgentRuntimeCreationContext? seenCreation = null;
        AgentRunContext? seenContext = null;
        var runner = new Mock<IAgentRunner>();
        runner.Setup(r => r.RunAsync(reference, It.IsAny<AgentRuntimeRunRequest>(),
                It.IsAny<AgentRuntimeCreationContext>(), It.IsAny<AgentRunContext>(),
                It.IsAny<CancellationToken>()))
            .Returns((AgentDefinitionReference definition, AgentRuntimeRunRequest run,
                AgentRuntimeCreationContext creation, AgentRunContext context, CancellationToken token) =>
            {
                seenRequest = run;
                seenCreation = creation;
                seenContext = context;
                return Stream();
            });
        var executor = new HeadlessAgentExecutor(templates.Object, catalog.Object, resolver.Object,
            new AgentRunContextFactory(), runner.Object);
        var result = await executor.ExecuteAsync(new HeadlessAgentRunRequest(id, "Hello"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("final answer", result.Text);
        Assert.Equal(seenContext!.RunId, result.AgentRunId);
        Assert.Null(seenContext.ConversationId);
        Assert.Null(seenContext.ParentRunId);
        Assert.Single(seenRequest!.Messages);
        Assert.Equal(AgentMessageRole.User, seenRequest.Messages[0].Role);
        Assert.Equal("Hello", seenRequest.Messages[0].Content);
        Assert.Equal(AgentRuntimeInvocationKind.UserMessage, seenRequest.InvocationKind);
        Assert.Empty(seenRequest.Inputs);
        Assert.Empty(seenRequest.Attachments);
        Assert.NotNull(seenCreation!.RuntimeResources.ToolApprovalCoordinator);
        Assert.NotNull(seenCreation.RuntimeResources.ToolApprovalPolicy);
    }

    [Fact]
    public void ProfilesAndMcpBindings_RequireExplicitPreflightRejection()
    {
        var descriptor = new AgentDefinitionDescriptor
        {
            Reference = new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, Guid.NewGuid().ToString("D")),
            Name = "agent", RuntimeKind = AgentRuntimeKind.LlmAgent
        };
        Assert.True(HeadlessAgentExecutor.HasUnsupportedCapabilities(
            new AgentTemplateDefinition { EnableFileMemory = true }, descriptor));
        Assert.True(HeadlessAgentExecutor.HasUnsupportedCapabilities(
            new AgentTemplateDefinition { EnableShell = true }, descriptor));
        Assert.False(HeadlessAgentExecutor.HasUnsupportedCapabilities(new AgentTemplateDefinition(), descriptor));
    }

    private static async IAsyncEnumerable<AgentRunEvent> Stream(
        [EnumeratorCancellation] CancellationToken token = default)
    {
        await Task.Yield();
        yield return new AgentTextDelta("msg", "agent", "ignored delta");
        yield return new AgentRunCompleted(new AgentRunResult
        {
            FinalMessage = new AgentOutputMessage("agent", "final answer")
        });
    }
}
