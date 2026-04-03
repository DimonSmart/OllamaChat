var workflow = WorkflowDefinitionBuilder
    .New("proposal-panel-concurrent", "Proposal Panel Concurrent")
    .Description("Three agents review the same user proposal in parallel and return a combined panel response.")
    .RunInteractively()
    .Agent("optimist", agent => agent
        .Role("Optimistic reviewer")
        .Summary("Looks for upside, opportunity, and leverage.")
        .UseDraft(
            AgentDefinitionBuilder
                .New("Optimistic Reviewer", "optimist")
                .WithInstructions(
                    @"Review the user's proposal from an optimistic angle.
Focus on upside, leverage, growth potential, and what could work unusually well.")
                .AutoSelectTools(0)
                .BuildDescription()))
    .Agent("skeptic", agent => agent
        .Role("Skeptical reviewer")
        .Summary("Looks for hidden risk, weak assumptions, and failure modes.")
        .UseDraft(
            AgentDefinitionBuilder
                .New("Skeptical Reviewer", "skeptic")
                .WithInstructions(
                    @"Review the user's proposal from a skeptical angle.
Focus on weak assumptions, execution risk, hidden cost, and likely failure modes.")
                .AutoSelectTools(0)
                .BuildDescription()))
    .Agent("operator", agent => agent
        .Role("Operational reviewer")
        .Summary("Focuses on execution sequence, dependencies, and decision checkpoints.")
        .UseDraft(
            AgentDefinitionBuilder
                .New("Operational Reviewer", "operator")
                .WithInstructions(
                    @"Review the user's proposal from an execution angle.
Focus on sequencing, dependencies, rollout checkpoints, and practical next steps.")
                .AutoSelectTools(0)
                .BuildDescription()))
    .UseConcurrent(concurrent => concurrent
        .Participants("optimist", "skeptic", "operator")
        .ConcatenateAllMessages())
    .Build();

workflow
