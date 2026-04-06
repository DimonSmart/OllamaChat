namespace ChatClient.Api.AgentWorkflows;

public static class WorkflowCodeTemplates
{
    public static string NewWorkflowScaffold { get; } =
        """"
        var workflow = WorkflowDefinitionBuilder
            .New("new-workflow", "New Workflow")
            .Description("Describe what this workflow should do.")
            .Agent("triage", agent => agent
                .Role("Entry point")
                .Summary("Handles the initial request and routes the next step when needed.")
                .UseDraft(
                    AgentTemplateBuilder
                        .New("New Workflow Triage", "triage")
                        .WithInstructions("""
                            Replace this scaffold with the real workflow instructions.
                            Keep the response focused on the workflow's entry step.
                            """)
                        .AutoSelectTools(0)
                        .Build()))
            .UseHandoff(handoff => handoff
                .StartWith("triage"))
            .Build();

        workflow
        """";
}
