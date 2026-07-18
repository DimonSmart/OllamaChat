using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;

namespace ChatClient.Tests;

public sealed class HandoffWorkflowDefinitionBuilderTests
{
    [Fact]
    [Obsolete]
    public void Build_CreatesWorkflowWithStartInputsAgentsAndHandoffs()
    {
        var workflow = HandoffWorkflowDefinitionBuilder
            .New("demo", "Demo Workflow")
            .Description("A test workflow.")
            .RunAutonomously(maxAutomaticTurns: 6, completionPhase: "complete", completionSummaryLabel: "final")
            .RequireDocument("resume", "Resume", static input => input.Description("Candidate resume."))
            .OptionalText("response_language", "Response Language", static input => input.DefaultValue("English"))
            .StartWith("triage")
            .Agent("triage", agent => agent
                .Role("Router")
                .Summary("Routes the conversation.")
                .UseDraft(CreateDraft("Triage", "triage"))
                .Capability("task-session-store", "Task session store", capability => capability
                    .Purpose("Read workflow state.")
                    .Availability(AgentWorkflowCapabilityAvailability.Available)
                    .AvailabilityNote("Built-in task session MCP server is available.")))
            .Agent("specialist", agent => agent
                .Role("Specialist")
                .Summary("Handles the main work.")
                .UseDraft(CreateDraft("Specialist", "specialist")))
            .Handoff("triage", "specialist", "start work")
            .Fallback("specialist", "triage")
            .Build();

        Assert.Equal("demo", workflow.Id);
        Assert.Equal("Demo Workflow", workflow.DisplayName);
        Assert.Equal("triage", workflow.StartAgentId);
        Assert.Equal(AgentWorkflowExecutionMode.Autonomous, workflow.Execution.Mode);
        Assert.Equal(6, workflow.Execution.MaxAutomaticTurns);
        Assert.Equal("complete", workflow.Execution.CompletionPhase);
        Assert.Equal("final", workflow.Execution.CompletionSummaryLabel);

        var resume = Assert.Single(workflow.StartInputs, static input => input.Key == "resume");
        Assert.Equal(WorkflowStartInputKind.MarkdownDocument, resume.Kind);
        Assert.True(resume.IsRequired);

        var responseLanguage = Assert.Single(workflow.StartInputs, static input => input.Key == "response_language");
        Assert.Equal("English", responseLanguage.DefaultValue);
        Assert.False(responseLanguage.IsRequired);

        var triage = Assert.Single(workflow.Agents, static agent => agent.Id == "triage");
        Assert.Equal("Router", triage.Role);
        Assert.Contains(triage.CapabilityRequirements, static requirement => requirement.Key == "task-session-store");

        Assert.Contains(workflow.Handoffs, static handoff =>
            handoff.FromAgentId == "triage" &&
            handoff.ToAgentId == "specialist" &&
            handoff.Label == "start work" &&
            !handoff.IsFallback);
        Assert.Contains(workflow.Handoffs, static handoff =>
            handoff.FromAgentId == "specialist" &&
            handoff.ToAgentId == "triage" &&
            handoff.IsFallback);
    }

    [Fact]
    [Obsolete]
    public void Build_ThrowsWhenStartAgentWasNotDefined()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            HandoffWorkflowDefinitionBuilder
                .New("demo", "Demo Workflow")
                .StartWith("missing")
                .Agent("triage", agent => agent
                    .Role("Router")
                    .UseDraft(CreateDraft("Triage", "triage")))
                .Build());

        Assert.Contains("start agent 'missing' is not defined", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Obsolete]
    public void Build_CreatesSavedAgentTemplateReferenceWithOverrides()
    {
        var savedAgentId = Guid.NewGuid();
        var workflow = HandoffWorkflowDefinitionBuilder
            .New("demo", "Demo Workflow")
            .StartWith("technical")
            .Agent("technical", agent => agent
                .UseAgent(savedAgentId)
                .Role("Technical interviewer")
                .OverrideName("Technical Interviewer")
                .OverrideInstructions("Run a technical interview."))
            .Build();

        var technical = Assert.Single(workflow.Agents);
        Assert.Equal("technical", technical.Id);
        Assert.Equal("Technical interviewer", technical.Role);
        Assert.Null(technical.AgentDraft);
        Assert.Null(technical.SavedAgentTemplate);
        var source = Assert.IsType<SavedDefinitionParticipantSource>(technical.Source);
        Assert.Equal(AgentDefinitionKind.SavedAgent, source.Reference.Kind);
        Assert.Equal(savedAgentId.ToString("D"), source.Reference.Id);
        Assert.Equal("Technical Interviewer", technical.Overrides.DisplayName);
        Assert.Equal("Run a technical interview.", technical.Overrides.Llm!.Instructions);
        Assert.Null(technical.DraftOverrides.AgentName);
        Assert.Null(technical.DraftOverrides.Instructions);
    }

    [Fact]
    [Obsolete]
    public void Build_CreatesSavedAgentTemplateReferenceWithAppendedInstructions()
    {
        var savedAgentId = Guid.NewGuid();
        var workflow = HandoffWorkflowDefinitionBuilder
            .New("demo", "Demo Workflow")
            .StartWith("technical")
            .Agent("technical", agent => agent
                .UseAgent(savedAgentId)
                .Role("Technical interviewer")
                .AppendInstructions("Stay focused on the current workflow step."))
            .Build();

        var technical = Assert.Single(workflow.Agents);
        Assert.Equal("Stay focused on the current workflow step.", technical.Overrides.Llm!.AppendedInstructions);
        Assert.Null(technical.Overrides.Llm.Instructions);
        Assert.Null(technical.DraftOverrides.AppendedInstructions);
        Assert.Null(technical.DraftOverrides.Instructions);
    }

    [Fact]
    [Obsolete]
    public void Build_ThrowsWhenInstructionsAndAppendInstructionsAreCombined()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            HandoffWorkflowDefinitionBuilder
                .New("demo", "Demo Workflow")
                .StartWith("technical")
                .Agent("technical", agent => agent
                    .UseAgent(Guid.NewGuid())
                    .Role("Technical interviewer")
                    .OverrideInstructions("Replace prompt.")
                    .AppendInstructions("Append prompt."))
                .Build());

        Assert.Contains("cannot use both OverrideInstructions and AppendInstructions", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Obsolete]
    public void Build_DefaultsSavedAgentRoleToDefinitionKindWhenRoleIsOmitted()
    {
        var workflow = HandoffWorkflowDefinitionBuilder
            .New("demo", "Demo Workflow")
            .StartWith("router")
            .Agent("router", agent => agent
                .UseAgent(Guid.NewGuid()))
            .Build();

        var agent = Assert.Single(workflow.Agents);
        Assert.Equal("router", agent.Id);
        Assert.Equal("SavedAgent", agent.Role);
    }

    [Fact]
    [Obsolete]
    public void Build_FromSavedAgentPreservesLegacyNameSemantics()
    {
        var workflow = HandoffWorkflowDefinitionBuilder
            .New("demo", "Demo Workflow")
            .StartWith("reviewer")
            .Agent("reviewer", agent => agent
                .FromSavedAgent("Code Reviewer"))
            .Build();

        var agent = Assert.Single(workflow.Agents);
        var source = Assert.IsType<SavedAgentNameParticipantSource>(agent.Source);
        Assert.Equal("Code Reviewer", source.SavedAgentName);
    }

    [Fact]
    [Obsolete]
    public void Build_UseAgentStringRejectsInvalidGuid()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            HandoffWorkflowDefinitionBuilder
                .New("demo", "Demo Workflow")
                .StartWith("reviewer")
                .Agent("reviewer", agent => agent
                    .UseAgent("Code Reviewer"))
                .Build());

        Assert.Equal("agentId", exception.ParamName);
        Assert.Contains("valid GUID", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Obsolete]
    public void Build_UseWorkflowStringRejectsInvalidGuid()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            HandoffWorkflowDefinitionBuilder
                .New("demo", "Demo Workflow")
                .StartWith("reviewer")
                .Agent("reviewer", agent => agent
                    .UseWorkflow("Nested Workflow"))
                .Build());

        Assert.Equal("workflowId", exception.ParamName);
        Assert.Contains("valid GUID", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Domain.Models.AgentTemplateDefinition CreateDraft(string name, string shortName) =>
        AgentTemplateBuilder
            .New(name, shortName)
            .WithInstructions("Test instructions.")
            .AutoSelectTools(0)
            .Build();
}
