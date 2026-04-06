var workflow = WorkflowDefinitionBuilder
    .New("research-brief-sequential", "Research Brief Sequential")
    .Description("Three-stage sequential workflow that turns a raw user request into a concise research brief.")
    .RunInteractively()
    .Agent("researcher", agent => agent
        .Role("Researcher")
        .Summary("Extracts the core question, assumptions, and missing evidence.")
        .UseDraft(
            AgentTemplateBuilder
                .New("Sequential Researcher", "researcher")
                .WithInstructions(
                    @"You are the first stage in a sequential workflow.
Read the user's request and produce a compact research outline with assumptions, evidence needed, and key unknowns.")
                .AutoSelectTools(0)
                .Build()))
    .Agent("critic", agent => agent
        .Role("Critical reviewer")
        .Summary("Finds weak reasoning, gaps, and risks in the outline.")
        .UseDraft(
            AgentTemplateBuilder
                .New("Sequential Critic", "critic")
                .WithInstructions(
                    @"You are the second stage in a sequential workflow.
Review the prior output critically. Highlight unsupported claims, risks, and missing tradeoffs.")
                .AutoSelectTools(0)
                .Build()))
    .Agent("writer", agent => agent
        .Role("Brief writer")
        .Summary("Produces the final concise brief for the user.")
        .UseDraft(
            AgentTemplateBuilder
                .New("Sequential Writer", "writer")
                .WithInstructions(
                    @"You are the final stage in a sequential workflow.
Turn the prior outputs into a crisp, user-facing brief with clear recommendations and caveats.")
                .AutoSelectTools(0)
                .Build()))
    .UseSequential(sequential => sequential
        .Order("researcher", "critic", "writer"))
    .Build();

workflow
