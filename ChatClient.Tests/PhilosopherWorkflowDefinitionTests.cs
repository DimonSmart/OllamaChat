using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Services.AgentRuntime;

namespace ChatClient.Tests;

public sealed class PhilosopherWorkflowDefinitionTests
{
    private static readonly Guid KantAgentId = Guid.Parse("ab38adc6-74a2-4ccc-924b-eb1bce9d0985");
    private static readonly Guid NietzscheAgentId = Guid.Parse("8bb2a12d-d5fd-440b-b622-b46d8897556a");

    [Fact]
    public async Task BuiltInWorkflow_UsesStableSavedAgentIds()
    {
        var sourcePath = Path.Combine(
            GetApiRoot(),
            "Data",
            "workflows",
            "philosopher-battle-group-chat.workflow.csx");
        var sourceCode = await File.ReadAllTextAsync(sourcePath);

        var compiled = await new WorkflowDefinitionCompiler().CompileAsync(sourceCode);
        var workflow = Assert.IsType<GroupChatWorkflowDefinition>(compiled.Workflow);

        AssertSavedAgentReference(workflow, "debater_a", KantAgentId);
        AssertSavedAgentReference(workflow, "debater_b", NietzscheAgentId);
        Assert.Contains("UseAgentById", sourceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("FromSavedAgent", sourceCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UseAgentByName_PreservesExpressiveNameBasedReference()
    {
        const string sourceCode =
            """
            var workflow = WorkflowDefinitionBuilder
                .New("name-based-demo", "Name-based Demo")
                .Agent("kant", agent => agent
                    .UseAgentByName("Immanuel Kant")
                    .Role("Kantian philosopher"))
                .UseSequential(sequential => sequential
                    .Order("kant"))
                .Build();

            workflow
            """;

        var compiled = await new WorkflowDefinitionCompiler().CompileAsync(sourceCode);
        var workflow = Assert.IsType<SequentialWorkflowDefinition>(compiled.Workflow);
        var participant = Assert.Single(workflow.Participants);
        var source = Assert.IsType<SavedAgentNameParticipantSource>(participant.Source);

        Assert.Equal("Immanuel Kant", source.SavedAgentName);
    }

    private static void AssertSavedAgentReference(
        GroupChatWorkflowDefinition workflow,
        string participantId,
        Guid expectedAgentId)
    {
        var participant = Assert.Single(
            workflow.Participants,
            candidate => string.Equals(candidate.Id, participantId, StringComparison.Ordinal));
        var source = Assert.IsType<SavedDefinitionParticipantSource>(participant.Source);

        Assert.Equal(AgentDefinitionKind.SavedAgent, source.Reference.Kind);
        Assert.Equal(expectedAgentId.ToString("D"), source.Reference.Id);
    }

    private static string GetApiRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ChatClient.Api"));
    }
}
