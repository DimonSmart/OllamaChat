using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Services.Agentic;

namespace ChatClient.Tests;

public sealed class WorkflowParticipantResolverTests
{
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
        var resolver = new WorkflowParticipantResolver(null!, null!);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(workflow, TestContext.Current.CancellationToken));

        Assert.Equal("Workflow participant 'reviewer' has no executable source.", error.Message);
    }
}
