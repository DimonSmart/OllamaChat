using ChatClient.Api.AgentWorkflows;
using ChatClient.Application.Services.Agentic;

namespace ChatClient.Tests;

public sealed class HandoffWorkflowDefinitionBuilderTests
{
    [Fact]
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
    public void Build_CreatesSavedAgentTemplateReferenceWithOverrides()
    {
        var workflow = HandoffWorkflowDefinitionBuilder
            .New("demo", "Demo Workflow")
            .StartWith("technical")
            .AgentFromSaved("Saved Technical Agent", agent => agent
                .Id("technical")
                .Role("Technical interviewer")
                .Name("Technical Interviewer")
                .Instructions("Run a technical interview."))
            .Build();

        var technical = Assert.Single(workflow.Agents);
        Assert.Equal("technical", technical.Id);
        Assert.Equal("Technical interviewer", technical.Role);
        Assert.Null(technical.AgentDraft);
        Assert.NotNull(technical.SavedAgentTemplate);
        Assert.Equal("Saved Technical Agent", technical.SavedAgentTemplate!.SavedAgentName);
        Assert.Equal("Technical Interviewer", technical.DraftOverrides.AgentName);
        Assert.Equal("Run a technical interview.", technical.DraftOverrides.Instructions);
    }

    [Fact]
    public void Build_CreatesSavedAgentTemplateReferenceWithAppendedInstructions()
    {
        var workflow = HandoffWorkflowDefinitionBuilder
            .New("demo", "Demo Workflow")
            .StartWith("technical")
            .AgentFromSaved("Saved Technical Agent", agent => agent
                .Id("technical")
                .Role("Technical interviewer")
                .AppendInstructions("Stay focused on the current workflow step."))
            .Build();

        var technical = Assert.Single(workflow.Agents);
        Assert.Equal("Stay focused on the current workflow step.", technical.DraftOverrides.AppendedInstructions);
        Assert.Null(technical.DraftOverrides.Instructions);
    }

    [Fact]
    public void Build_ThrowsWhenInstructionsAndAppendInstructionsAreCombined()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            HandoffWorkflowDefinitionBuilder
                .New("demo", "Demo Workflow")
                .StartWith("technical")
                .AgentFromSaved("Saved Technical Agent", agent => agent
                    .Id("technical")
                    .Role("Technical interviewer")
                    .Instructions("Replace prompt.")
                    .AppendInstructions("Append prompt."))
                .Build());

        Assert.Contains("cannot use both Instructions and AppendInstructions", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_DefaultsSavedAgentRoleToTemplateNameWhenRoleIsOmitted()
    {
        var workflow = HandoffWorkflowDefinitionBuilder
            .New("demo", "Demo Workflow")
            .StartWith("Saved Router")
            .AgentFromSaved("Saved Router")
            .Build();

        var agent = Assert.Single(workflow.Agents);
        Assert.Equal("Saved Router", agent.Id);
        Assert.Equal("Saved Router", agent.Role);
    }

    private static Domain.Models.AgentDescription CreateDraft(string name, string shortName) =>
        AgentDefinitionBuilder
            .New(name, shortName)
            .WithInstructions("Test instructions.")
            .AutoSelectTools(0)
            .BuildDescription();
}
