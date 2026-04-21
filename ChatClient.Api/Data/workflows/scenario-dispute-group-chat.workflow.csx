var workflow = WorkflowDefinitionBuilder
    .New("scenario-dispute-group-chat", "Scenario Dispute Group Chat")
    .Description("Autonomous two-sided dispute debate over a user-defined scenario, with participant briefs supplied at runtime and a judge focused on debate quality.")
    .RunAutonomously(maxAutomaticTurns: 10, completionPhase: "complete", completionSummaryLabel: "final")
    .RequireDocument("scenario", "Scenario", input => input
        .Description("The shared situation or scene that creates the dispute. Describe only the fixed starting facts."))
    .OptionalText("participant_a_name", "Participant A Name", input => input
        .Description("Display name for the first side of the dispute.")
        .DefaultValue("Participant A"))
    .RequireDocument("participant_a_position", "Participant A Position", input => input
        .Description("Who participant A is, what participant A claims, what outcome participant A wants, and what starting advantages or constraints participant A has."))
    .OptionalText("participant_b_name", "Participant B Name", input => input
        .Description("Display name for the second side of the dispute.")
        .DefaultValue("Participant B"))
    .RequireDocument("participant_b_position", "Participant B Position", input => input
        .Description("Who participant B is, what participant B claims, what outcome participant B wants, and what starting advantages or constraints participant B has."))
    .OptionalDocument("debate_rules", "Debate Rules", input => input
        .Description("Extra rules or constraints for this battle.")
        .DefaultValue(
            @"- Treat the scenario as the fixed shared starting record.
- Each participant may introduce up to two plausible supplemental facts, examples, or pieces of claimed evidence if a detail is missing.
- Supplemental additions may strengthen a side's case, but they must not rewrite or directly contradict the established scenario.
- Attack weak assumptions, contradictions, and unsupported additions directly.
- Judge debate quality, not objective legal or moral truth."))
    .OptionalText("battle_language", "Battle Language", input => input
        .Description("Language used by the host, participants, and judge.")
        .DefaultValue("Use the same language as the scenario description."))
    .Agent("host", agent => agent
        .Role("Dispute debate host")
        .OverrideAvatarText("H")
        .Summary("Frames the scenario, names both sides, and opens the dispute.")
        .UseDraft(
            AgentTemplateBuilder
                .New("Scenario Dispute Host", "host")
                .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, binding => binding
                    .Enabled()
                    .OnlyTools(
                        "session_get_document",
                        "session_get_parameter"))
                .WithInstructions(
                    @"You are the opening host of a structured dispute debate.

Before speaking, read scenario, participant_a_position, participant_b_position, and debate_rules with session_get_document.
Read participant_a_name, participant_b_name, and battle_language with session_get_parameter.
If battle_language says to use the same language as the scenario, mirror the scenario language.
Open the debate in 3-5 sentences.
State the shared scenario crisply, name both sides, summarize each side's starting position fairly, and mention any battle rule that materially affects the debate.
End by explicitly inviting participant_a_name and participant_b_name to argue.

You speak only in the opening turn. Do not summarize the debate.")
                .AutoSelectTools(0)
                .Build()))
    .Agent("participant_a", agent => agent
        .Role("Participant A advocate")
        .OverrideAvatarText("A")
        .Summary("Defends participant A's brief and attacks participant B's weakest claims.")
        .UseDraft(
            AgentTemplateBuilder
                .New("Participant A Advocate", "participant_a")
                .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, binding => binding
                    .Enabled()
                    .OnlyTools(
                        "session_get_document",
                        "session_get_parameter"))
                .WithInstructions(
                    @"You are the advocate for participant A in a structured dispute debate.

Before every turn, read scenario, participant_a_position, participant_b_position, and debate_rules with session_get_document.
Read participant_a_name, participant_b_name, and battle_language with session_get_parameter.
If battle_language says to use the same language as the scenario, mirror the scenario language.
Speak as participant_a_name and defend only participant_a_position.
Treat the scenario as the shared starting record.
You may introduce at most two plausible supplemental facts, examples, or pieces of claimed evidence if the scenario leaves a gap. Use them to strengthen your case, not to rewrite the scenario. Do not contradict fixed facts from the scenario or your own prior claims.
Challenge participant_b_name directly on weak reasoning, contradictions, and unsupported additions.
Keep each turn to 3-5 sentences, focused on the decisive clash.
Speak to the other participants, not the user. Do not ask the user for input or summarize the whole debate.")
                .AutoSelectTools(0)
                .Build()))
    .Agent("participant_b", agent => agent
        .Role("Participant B advocate")
        .OverrideAvatarText("B")
        .Summary("Defends participant B's brief and attacks participant A's weakest claims.")
        .UseDraft(
            AgentTemplateBuilder
                .New("Participant B Advocate", "participant_b")
                .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, binding => binding
                    .Enabled()
                    .OnlyTools(
                        "session_get_document",
                        "session_get_parameter"))
                .WithInstructions(
                    @"You are the advocate for participant B in a structured dispute debate.

Before every turn, read scenario, participant_a_position, participant_b_position, and debate_rules with session_get_document.
Read participant_a_name, participant_b_name, and battle_language with session_get_parameter.
If battle_language says to use the same language as the scenario, mirror the scenario language.
Speak as participant_b_name and defend only participant_b_position.
Treat the scenario as the shared starting record.
You may introduce at most two plausible supplemental facts, examples, or pieces of claimed evidence if the scenario leaves a gap. Use them to strengthen your case, not to rewrite the scenario. Do not contradict fixed facts from the scenario or your own prior claims.
Challenge participant_a_name directly on weak reasoning, contradictions, and unsupported additions.
Keep each turn to 3-5 sentences, focused on the decisive clash.
Speak to the other participants, not the user. Do not ask the user for input or summarize the whole debate.")
                .AutoSelectTools(0)
                .Build()))
    .Agent("judge", agent => agent
        .Role("Debate quality judge")
        .OverrideAvatarText("J")
        .Summary("Scores the debate on professional debate-quality criteria and closes the workflow.")
        .UseDraft(
            AgentTemplateBuilder
                .New("Scenario Dispute Judge", "judge")
                .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, binding => binding
                    .Enabled()
                    .OnlyTools(
                        "session_get_document",
                        "session_get_parameter",
                        "session_save_summary",
                        "session_set_phase"))
                .WithInstructions(
                    @"You are the final judge of a structured dispute debate.

Before speaking, read scenario, participant_a_position, participant_b_position, and debate_rules with session_get_document.
Read participant_a_name, participant_b_name, and battle_language with session_get_parameter.
If battle_language says to use the same language as the scenario, mirror the scenario language.
Judge who debated better, not which real-world side is objectively true.

Score each participant from 1 to 10 on these six criteria:
1. Organization and clarity
2. Evidence and reasoning
3. Analysis of the core dispute
4. Refutation and defence
5. Rhetorical delivery
6. Adaptive creativity under ambiguity

Treat criterion 6 as the disciplined use of plausible supplemental material without contradicting the shared scenario.

Deliver a concise verdict in battle_language with:
- winner
- one scorecard line per participant showing the six criteria and the total
- strongest move from each participant
- one improvement note for each participant

Persist the verdict with session_save_summary using label final.
Mark the workflow complete with session_set_phase.

Speak only once, at the end of the debate.")
                .AutoSelectTools(0)
                .Build()))
    .UseGroupChat(groupChat => groupChat
        .Participants("host", "participant_a", "participant_b", "judge")
        .UseProgrammableManager(manager => manager
            .MaximumIterations(10)
            .Program(GroupChatManagerPrograms.PrefixCycleSuffix(
                prefix: new[] { "host" },
                cycle: new[] { "participant_a", "participant_b" },
                suffix: new[] { "participant_a", "participant_b", "judge" }))))
    .Build();

workflow
