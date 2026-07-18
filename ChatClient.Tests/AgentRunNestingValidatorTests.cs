using ChatClient.Application.Services.AgentRuntime;

namespace ChatClient.Tests;

public sealed class AgentRunNestingValidatorTests
{
    [Fact]
    public void Validate_RootWorkflow_AllowsSingleFrame()
    {
        var target = Workflow("a", "A");
        var context = Context([Frame(target)]);

        var result = CreateValidator().Validate(target, context);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WorkflowDepthAtLimit_IsAllowed()
    {
        var target = Workflow("b", "B");
        var context = Context([
            Frame(Workflow("a", "A")),
            Frame(target, "implementation", "Implementation")
        ]);

        var result = CreateValidator(2).Validate(target, context);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WorkflowDepthAboveLimit_IsRejected()
    {
        var target = Workflow("c", "C");
        var context = Context([
            Frame(Workflow("a", "A")),
            Frame(Workflow("b", "B")),
            Frame(target)
        ]);

        var result = CreateValidator(2).Validate(target, context);

        Assert.False(result.IsValid);
        Assert.Equal("workflow_nesting_limit_exceeded", result.Error?.Code);
    }

    [Fact]
    public void Validate_LlmAgent_DoesNotIncreaseWorkflowDepth()
    {
        var target = Agent("leaf", "Leaf");
        var context = Context([
            Frame(Workflow("a", "A")),
            Frame(Workflow("b", "B")),
            Frame(target, "leaf", "Leaf")
        ]);

        var result = CreateValidator(2).Validate(target, context);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    [InlineData("c")]
    public void Validate_RecursiveWorkflowReference_IsRejected(string repeatedId)
    {
        var target = Workflow(repeatedId, repeatedId.ToUpperInvariant());
        var context = Context([
            Frame(Workflow("a", "A")),
            Frame(Workflow("b", "B"), "review", "Review"),
            Frame(Workflow("c", "C")),
            Frame(target, "again", "Again")
        ]);

        var result = CreateValidator().Validate(target, context);

        Assert.False(result.IsValid);
        Assert.Equal("workflow_cycle_detected", result.Error?.Code);
    }

    [Fact]
    public void Validate_RepeatedSiblingReference_IsAllowedWhenNotInActiveStack()
    {
        var target = Workflow("b", "B");
        var firstSiblingContext = Context([
            Frame(Workflow("a", "A")),
            Frame(target, "first", "First")
        ]);
        var secondSiblingContext = Context([
            Frame(Workflow("a", "A")),
            Frame(target, "second", "Second")
        ]);

        Assert.True(CreateValidator().Validate(target, firstSiblingContext).IsValid);
        Assert.True(CreateValidator().Validate(target, secondSiblingContext).IsValid);
    }

    [Fact]
    public void Validate_CycleMessageContainsDisplayNamesAndParticipant()
    {
        var target = Workflow("a", "Release Review");
        var context = Context([
            Frame(target),
            Frame(Workflow("b", "Implementation Pipeline"), "implementation", "Implementation"),
            Frame(target, "review", "Review")
        ]);

        var result = CreateValidator().Validate(target, context);

        Assert.False(result.IsValid);
        Assert.Contains("Release Review", result.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("Implementation Pipeline", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("Review", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal("review", result.Error.Metadata["participant.id"]);
    }

    private static AgentRunNestingValidator CreateValidator(int maximumDepth = 8) =>
        new(new AgentRuntimeOptions { MaximumWorkflowNestingDepth = maximumDepth });

    private static AgentRunContext Context(IReadOnlyList<AgentRunFrame> stack) =>
        new()
        {
            RunId = Guid.NewGuid().ToString("N"),
            DefinitionStack = stack
        };

    private static AgentRunFrame Frame(
        AgentDefinitionDescriptor descriptor,
        string? participantId = null,
        string? participantDisplayName = null) =>
        new()
        {
            Definition = descriptor.Reference,
            DisplayName = descriptor.Name,
            ParticipantId = participantId,
            ParticipantDisplayName = participantDisplayName
        };

    private static AgentDefinitionDescriptor Workflow(string id, string name) =>
        Descriptor(id, name, AgentDefinitionKind.SavedWorkflow, AgentRuntimeKind.WorkflowAgent);

    private static AgentDefinitionDescriptor Agent(string id, string name) =>
        Descriptor(id, name, AgentDefinitionKind.SavedAgent, AgentRuntimeKind.LlmAgent);

    private static AgentDefinitionDescriptor Descriptor(
        string id,
        string name,
        AgentDefinitionKind definitionKind,
        AgentRuntimeKind runtimeKind) =>
        new()
        {
            Reference = new AgentDefinitionReference(definitionKind, id),
            Name = name,
            RuntimeKind = runtimeKind,
            ModelRequirement = AgentModelRequirement.Required
        };
}
