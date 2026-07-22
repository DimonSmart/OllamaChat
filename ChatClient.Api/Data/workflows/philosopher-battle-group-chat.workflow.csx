var workflow = WorkflowDefinitionBuilder
    .New("philosopher-battle-group-chat", "Philosopher Battle Group Chat")
    .Description("Autonomous philosophical debate coordinated by a workflow-defined group chat program with a host-led opening and a judge-led closing verdict.")
    .RunAutonomously(maxAutomaticTurns: 10, completionPhase: "complete", completionSummaryLabel: "final")
    .RequireText("opening_topic", "Opening Topic", input => input
        .Description("The central philosophical question for the debate."))
    .OptionalText("battle_language", "Battle Language", input => input
        .Description("Language used by the host and judge.")
        .DefaultValue("English"))
    .OptionalText("verdict_focus", "Verdict Focus", input => input
        .Description("Extra emphasis for the final judge verdict.")
        .DefaultValue("Name the strongest argument, the weakest move, and the unresolved tension."))
    .Agent("host", agent => agent
        .Role("Debate host")
        .OverrideAvatarText("H")
        .Summary("Opens the debate by framing the topic sharply and setting the tone.")
        .UseDraft(
            AgentTemplateBuilder
                .New("Debate Host", "host")
                .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, binding => binding
                    .Enabled()
                    .OnlyTools("session_get_parameter"))
                .WithInstructions(
                    @"You are the opening host of a philosopher debate.

Before speaking, read the workflow parameters opening_topic and battle_language with session_get_parameter.
Open the debate in battle_language.
Frame the topic as a sharp conflict in 2-4 sentences.
End by explicitly inviting {{agent:debater_a.displayName}} and {{agent:debater_b.displayName}} to clash.

You speak only in the opening turn. Do not summarize the debate.")
                .AutoSelectTools(0)
                .Build()))
    .Agent("debater_a", agent => agent
        .UseAgent("ab38adc6-74a2-4ccc-924b-eb1bce9d0985")
        .Role("Kantian philosopher")
        .OverrideAvatarText("K")
        .AppendInstructions(
            @"Workflow mode:
- This is an autonomous philosopher debate.
- Speak to the other participants, not to the user.
- Do not ask the user for input or summarize the whole debate."))
    .Agent("debater_b", agent => agent
        .UseAgent("8bb2a12d-d5fd-440b-b622-b46d8897556a")
        .Role("Nietzschean philosopher")
        .OverrideAvatarText("N")
        .AppendInstructions(
            @"Workflow mode:
- This is an autonomous philosopher debate.
- Speak to the other participants, not to the user.
- Do not ask the user for input or summarize the whole debate."))
    .Agent("judge", agent => agent
        .Role("Debate judge")
        .OverrideAvatarText("J")
        .Summary("Closes the debate with a concise verdict and persists the final summary.")
        .UseDraft(
            AgentTemplateBuilder
                .New("Debate Judge", "judge")
                .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, binding => binding
                    .Enabled()
                    .OnlyTools(
                        "session_get_parameter",
                        "session_save_summary",
                        "session_set_phase"))
                .WithInstructions(
                    @"You are the final judge of a philosopher debate.

Before speaking, read battle_language and verdict_focus with session_get_parameter.
Deliver the final verdict in battle_language.
Name the strongest argument, the weakest move, and the unresolved tension.
Persist the verdict with session_save_summary using label final.
Mark the workflow complete with session_set_phase.

Speak only once, at the end of the debate.")
                .AutoSelectTools(0)
                .Build()))
    .UseGroupChat(groupChat => groupChat
        .Participants("host", "debater_a", "debater_b", "judge")
        .UseProgrammableManager(manager => manager
            .MaximumIterations(10)
            .Program(GroupChatManagerPrograms.PrefixCycleSuffix(
                prefix: new[] { "host" },
                cycle: new[] { "debater_a", "debater_b" },
                suffix: new[] { "debater_a", "debater_b", "judge" }))))
    .Build();

workflow
