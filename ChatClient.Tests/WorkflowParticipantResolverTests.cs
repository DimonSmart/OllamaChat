using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;
using Moq;

namespace ChatClient.Tests;

public sealed class WorkflowParticipantResolverTests
{
    [Fact]
    public async Task ResolveAsync_SavedAgentUsesTargetedLookupInsteadOfCatalogEnumeration()
    {
        var agentId = Guid.NewGuid();
        var agents = new Mock<IAgentTemplateService>();
        agents.Setup(service => service.GetByIdAsync(agentId)).ReturnsAsync(new AgentTemplateDefinition
        {
            Id = agentId,
            AgentName = "Saved agent",
            Content = "Instructions"
        });
        var catalog = new Mock<IAgentDefinitionCatalog>(MockBehavior.Strict);
        var resolver = new WorkflowParticipantResolver(
            agents.Object, catalog.Object, new WorkflowDefinitionValidator());
        var workflow = new SequentialWorkflowDefinition
        {
            Id = "saved-agent",
            DisplayName = "Saved agent workflow",
            Participants =
            [
                new WorkflowParticipantDefinition
                {
                    Id = "writer",
                    Source = new SavedDefinitionParticipantSource(new AgentDefinitionReference(
                        AgentDefinitionKind.SavedAgent, agentId.ToString("D")))
                }
            ],
            ParticipantOrder = ["writer"]
        };

        var result = await resolver.ResolveAsync(workflow, TestContext.Current.CancellationToken);

        Assert.Equal("writer", Assert.Single(result).ParticipantId);
        agents.Verify(service => service.GetByIdAsync(agentId), Times.Once);
        agents.Verify(service => service.GetAllAsync(), Times.Never);
        catalog.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResolveAsync_ThrowsValidationErrorWhenParticipantSourceIsMissing()
    {
        var workflow = new SequentialWorkflowDefinition
        {
            Id = "missing-source",
            DisplayName = "Missing source",
            Participants =
            [
                new WorkflowParticipantDefinition
                {
                    Id = "reviewer"
                }
            ],
            ParticipantOrder = ["reviewer"]
        };
        var resolver = new WorkflowParticipantResolver(null!, null!, new WorkflowDefinitionValidator());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(workflow, TestContext.Current.CancellationToken));

        Assert.Equal("Workflow participant 'reviewer' has no executable source.", error.Message);
    }
}
