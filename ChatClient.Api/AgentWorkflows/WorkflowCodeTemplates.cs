using ChatClient.Domain.Models;

namespace ChatClient.Api.AgentWorkflows;

public sealed record WorkflowStarterTemplate(
    string WorkflowId,
    string DisplayName,
    string Kind,
    string SourceCode);

public static class WorkflowCodeTemplates
{
    public static IReadOnlyList<WorkflowStarterTemplate> StarterTemplates => CreateStarterTemplates();

    public static WorkflowStarterTemplate DefaultStarter =>
        GetRequiredStarter("philosopher-battle-group-chat");

    public static string NewWorkflowScaffold { get; } =
        """"
        var workflow = WorkflowDefinitionBuilder
            .New("new-workflow", "New Workflow")
            .Description("Describe what this workflow should do.")
            .Agent("triage", agent => agent
                .Role("Entry point")
                .Summary("Handles the initial request and routes the next step when needed.")
                .UseDraft(
                    AgentDefinitionBuilder
                        .New("New Workflow Triage", "triage")
                        .WithInstructions("""
                            Replace this scaffold with the real workflow instructions.
                            Keep the response focused on the workflow's entry step.
                            """)
                        .AutoSelectTools(0)
                        .BuildDescription()))
            .UseHandoff(handoff => handoff
                .StartWith("triage"))
            .Build();

        workflow
        """";

    public static WorkflowStarterTemplate GetRequiredStarter(string workflowId) =>
        StarterTemplates.First(template =>
            string.Equals(template.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase));

    public static string InterviewCoachHandoff { get; } =
        """"
        var workflow = WorkflowDefinitionBuilder
            .New("interview-coach-fixed-handoff", "Interview Coach Handoff")
            .Description("Specialized conversational flow with one entry router, sequential specialists, explicit fallback edges to triage, and required start inputs collected before the workflow begins.")
            .RequireDocument("resume", "Resume", input => input
                .Description("Candidate resume in markdown format."))
            .RequireDocument("job_description", "Job Description", input => input
                .Description("Target job description in markdown format."))
            .Agent("triage", agent => agent
                .Role("Router / entry point")
                .Summary("Owns the first turn, reads the shared session state when needed, decides which specialist should take over next, and receives fallback handoffs when the user goes off script.")
                .UseDraft(
                    AgentDefinitionBuilder
                        .New("Interview Coach Triage", "triage")
                        .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, binding => binding
                            .Enabled()
                            .OnlyTools("session_get"))
                        .WithInstructions("""
                            You are the triage agent for a staged interview workflow.
                            Your only responsibility is routing the conversation to the correct specialist.

                            Phases:
                            1. Receptionist: validate the required workflow start inputs.
                            2. Behavioural interviewer: run behavioural questions.
                            3. Technical interviewer: run technical questions.
                            4. Summarizer: generate the wrap-up.

                            Routing rules:
                            - Inspect shared session state with session_get when it is useful.
                            - Start with the receptionist when intake is incomplete or required workflow start inputs are missing.
                            - Route to the behavioural interviewer once intake is complete and the interview has not started.
                            - Route to the technical interviewer after the behavioural phase is complete.
                            - Route to the summarizer when the user wants to stop or both interview phases are complete.
                            - If a specialist falls back with an out-of-order request, decide the next best specialist without trying to answer in detail yourself.

                            Never conduct the interview yourself.
                            Be brief, predictable, and explicit about who is taking over next.
                            """)
                        .AutoSelectTools(0)
                        .BuildDescription())
                .Capability("task-session-store", "Task session store", capability => capability
                    .Purpose("Read-only access to current phase and stored inputs so routing stays deterministic across turns.")
                    .Availability(AgentWorkflowCapabilityAvailability.Available)
                    .AvailabilityNote("A generic task session MCP server is available for shared workflow state.")))
            .Agent("receptionist", agent => agent
                .Role("Start-input validation and session setup")
                .Summary("Verifies that the required workflow inputs are already attached to the shared session, then marks intake complete and hands over to the behavioural interviewer.")
                .UseDraft(
                    AgentDefinitionBuilder
                        .New("Interview Coach Receptionist", "receptionist")
                        .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, binding => binding
                            .Enabled()
                            .OnlyTools(
                                "session_get",
                                "session_set_phase",
                                "session_get_document",
                                "session_append_turn"))
                        .WithInstructions("""
                            You are the receptionist agent for an interview coach workflow.
                            Your job is to prepare the interview context before any questioning begins.

                            Required workflow start inputs:
                            - Resume (kind: resume)
                            - Job Description (kind: job_description)

                            Responsibilities:
                            - The shared task session already exists before chat starts. Read it with session_get before deciding whether intake is complete.
                            - The workflow start form already collected the required inputs before chat began.
                            - Verify that the required documents are present by inspecting session_get and session_get_document.
                            - Use session_append_turn for concise intake notes or decisions when useful, not as a duplicate log of every utterance.
                            - Set the workflow phase with session_set_phase when intake becomes complete.
                            - Handoff to the behavioural interviewer when all required start inputs are present and the user is ready.
                            - If a required start input is missing, tell the user exactly which one is missing and ask them to restart the workflow using the start form. Do not ask for or parse missing documents inside the chat.
                            - Fallback to triage only for unexpected requests outside the intake phase.

                            Do not conduct the interview yourself. Do not summarize the whole session. Stay in the intake role.
                            """)
                        .AutoSelectTools(0)
                        .BuildDescription())
                .Capability("task-session-store", "Task session store", capability => capability
                    .Purpose("Persistent shared state for workflow inputs, transcript turns, phase, and summary.")
                    .Availability(AgentWorkflowCapabilityAvailability.Available)
                    .AvailabilityNote("A generic task session MCP server is available for shared workflow state.")))
            .Agent("behavioural", agent => agent
                .Role("Behavioural interviewer")
                .Summary("Runs the behavioural phase, keeps transcript state, and hands over when the user is ready for technical questions.")
                .UseDraft(
                    AgentDefinitionBuilder
                        .New("Interview Coach Behavioural Interviewer", "behavioural")
                        .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, binding => binding
                            .Enabled()
                            .OnlyTools(
                                "session_get",
                                "session_get_document",
                                "session_append_turn",
                                "session_list_turns",
                                "session_set_phase"))
                        .WithInstructions("""
                            You are the behavioural interviewer in a staged interview coach workflow.
                            Your job is to run the behavioural phase and nothing else.

                            Responsibilities:
                            - Read the shared session with session_get and the stored documents with session_get_document before asking questions.
                            - Use the intake context to ask behavioural questions tailored to the user's experience and target role.
                            - Keep the exchange focused on one question at a time.
                            - Give short constructive feedback after each answer when appropriate.
                            - Use session_append_turn for concise observations or coaching notes when they are useful for later phases.
                            - Decide when the behavioural phase is complete and mark it with session_set_phase.
                            - Handoff to the technical interviewer when the user is ready for the next phase.
                            - Fallback to triage only for unexpected requests that break the phase flow.

                            Do not restart intake and do not generate the final summary.
                            """)
                        .AutoSelectTools(0)
                        .BuildDescription())
                .Capability("task-session-store", "Task session store", capability => capability
                    .Purpose("Persistent transcript and phase state across turns.")
                    .Availability(AgentWorkflowCapabilityAvailability.Available)
                    .AvailabilityNote("A generic task session MCP server is available for shared workflow state.")))
            .Agent("technical", agent => agent
                .Role("Technical interviewer")
                .Summary("Runs role-specific technical questions, appends transcript state, and hands over to the summarizer.")
                .UseDraft(
                    AgentDefinitionBuilder
                        .New("Interview Coach Technical Interviewer", "technical")
                        .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, binding => binding
                            .Enabled()
                            .OnlyTools(
                                "session_get",
                                "session_get_document",
                                "session_append_turn",
                                "session_list_turns",
                                "session_set_phase"))
                        .WithInstructions("""
                            You are the technical interviewer in a staged interview coach workflow.
                            Your job is to run the technical phase and nothing else.

                            Responsibilities:
                            - Read the shared session with session_get and the stored documents with session_get_document before asking questions.
                            - Use the intake context and earlier interview state to ask role-specific technical questions.
                            - Keep the exchange focused on one question at a time.
                            - Give short corrective feedback when the answer is weak or incomplete.
                            - Use session_append_turn for concise observations or coaching notes when they are useful for the final summary.
                            - Decide when the technical phase is complete and mark it with session_set_phase.
                            - Handoff to the summarizer when the user wants to wrap up or the phase is complete.
                            - Fallback to triage only for unexpected requests that break the phase flow.

                            Do not redo behavioural intake and do not produce the final report yourself.
                            """)
                        .AutoSelectTools(0)
                        .BuildDescription())
                .Capability("task-session-store", "Task session store", capability => capability
                    .Purpose("Persistent transcript and phase state across turns.")
                    .Availability(AgentWorkflowCapabilityAvailability.Available)
                    .AvailabilityNote("A generic task session MCP server is available for shared workflow state.")))
            .Agent("summarizer", agent => agent
                .Role("Wrap-up and summary")
                .Summary("Builds the final interview summary, marks the interview complete, and can return control to triage for a new request.")
                .UseDraft(
                    AgentDefinitionBuilder
                        .New("Interview Coach Summarizer", "summarizer")
                        .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, binding => binding
                            .Enabled()
                            .OnlyTools(
                                "session_get",
                                "session_get_document",
                                "session_list_turns",
                                "session_append_turn",
                                "session_save_summary",
                                "session_set_phase"))
                        .WithInstructions("""
                            You are the summarizer in a staged interview coach workflow.
                            Your job is to close the interview with a useful final summary.

                            Responsibilities:
                            - Review the accumulated interview state and transcript using session_get and session_list_turns.
                            - Read any stored documents you need with session_get_document.
                            - Produce a concise final assessment with strengths, weaknesses, and next-step recommendations.
                            - Persist the final summary with session_save_summary and mark the workflow phase with session_set_phase.
                            - Use session_append_turn only for a short closing note or structured follow-up pointer when useful.
                            - Handoff back to triage only if the user asks for a new activity after the wrap-up.

                            Do not continue the interview phase yourself and do not restart intake.
                            """)
                        .AutoSelectTools(0)
                        .BuildDescription())
                .Capability("task-session-store", "Task session store", capability => capability
                    .Purpose("Read/write access to the transcript and final summary.")
                    .Availability(AgentWorkflowCapabilityAvailability.Available)
                    .AvailabilityNote("A generic task session MCP server is available for shared workflow state.")))
            .UseHandoff(handoff => handoff
                .StartWith("triage")
                .Handoff("triage", "receptionist", "start / intake")
                .Handoff("triage", "behavioural", "resume interview")
                .Handoff("triage", "technical", "resume technical")
                .Handoff("triage", "summarizer", "wrap up")
                .Handoff("receptionist", "behavioural", "handoff after intake")
                .Fallback("receptionist", "triage")
                .Handoff("behavioural", "technical", "handoff after behavioural")
                .Fallback("behavioural", "triage")
                .Handoff("technical", "summarizer", "handoff after technical")
                .Fallback("technical", "triage")
                .Fallback("summarizer", "triage", "post-summary fallback"))
            .Build();

        workflow
        """";

    public static string PhilosopherBattleGroupChat { get; } =
        """"
        var workflow = WorkflowDefinitionBuilder
            .New("philosopher-battle-group-chat", "Philosopher Battle Group Chat")
            .Description("Autonomous philosophical debate coordinated by a custom group chat manager with a host-led opening and a judge-led closing verdict.")
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
                .AvatarText("H")
                .Summary("Opens the debate by framing the topic sharply and setting the tone.")
                .UseDraft(
                    AgentDefinitionBuilder
                        .New("Debate Host", "host")
                        .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, binding => binding
                            .Enabled()
                            .OnlyTools("session_get_parameter"))
                        .WithInstructions("""
                            You are the opening host of a philosopher debate.

                            Before speaking, read the workflow parameters opening_topic and battle_language with session_get_parameter.
                            Open the debate in battle_language.
                            Frame the topic as a sharp conflict in 2-4 sentences.
                            End by explicitly inviting Kant and Nietzsche to clash.

                            You speak only in the opening turn. Do not summarize the debate.
                            """)
                        .AutoSelectTools(0)
                        .BuildDescription()))
            .AgentFromSaved("Immanuel Kant", agent => agent
                .Id("kant")
                .Role("Kantian philosopher")
                .AvatarText("K")
                .Summary("Defends duty, autonomy, universality, and moral law.")
                .AppendInstructions("""
                    Workflow mode:
                    - This is an autonomous philosopher debate.
                    - Speak to the other participants, not to the user.
                    - Do not ask the user for input or summarize the whole debate.
                    """))
            .AgentFromSaved("Friedrich Nietzsche", agent => agent
                .Id("nietzsche")
                .Role("Nietzschean philosopher")
                .AvatarText("N")
                .Summary("Attacks herd morality, comfort, and universal rules.")
                .AppendInstructions("""
                    Workflow mode:
                    - This is an autonomous philosopher debate.
                    - Speak to the other participants, not to the user.
                    - Do not ask the user for input or summarize the whole debate.
                    """))
            .Agent("judge", agent => agent
                .Role("Debate judge")
                .AvatarText("J")
                .Summary("Closes the debate with a concise verdict and persists the final summary.")
                .UseDraft(
                    AgentDefinitionBuilder
                        .New("Debate Judge", "judge")
                        .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, binding => binding
                            .Enabled()
                            .OnlyTools(
                                "session_get_parameter",
                                "session_save_summary",
                                "session_set_phase"))
                        .WithInstructions("""
                            You are the final judge of a philosopher debate.

                            Before speaking, read battle_language and verdict_focus with session_get_parameter.
                            Deliver the final verdict in battle_language.
                            Name the strongest argument, the weakest move, and the unresolved tension.
                            Persist the verdict with session_save_summary using label final.
                            Mark the workflow complete with session_set_phase.

                            Speak only once, at the end of the debate.
                            """)
                        .AutoSelectTools(0)
                        .BuildDescription()))
            .UseGroupChat(groupChat => groupChat
                .Participants("host", "kant", "nietzsche", "judge")
                .UseCustomManager("philosopher-debate", maximumIterations: 10))
            .Build();

        workflow
        """";

    public static string PhilosopherBattleHandoff { get; } =
        """"
        var workflow = WorkflowDefinitionBuilder
            .New("philosopher-battle-handoff", "Philosopher Battle: Kant vs Nietzsche")
            .Description("Autonomous multi-topic philosophical battle moderated by a host. The user provides an opening topic, the philosophers respond to each other, the host changes topic when energy drops, and the host closes with a detailed quoted verdict.")
            .RunAutonomously(maxAutomaticTurns: 18, completionPhase: "complete", completionSummaryLabel: "final")
            .RequireText("opening_topic", "Opening Topic", input => input
                .Description("The first topic the host should throw into the battle."))
            .OptionalNumber("themes_to_cover", "Themes To Cover", input => input
                .Description("How many distinct themes the host should cover before wrapping up.")
                .DefaultValue("3"))
            .OptionalText("battle_language", "Battle Language", input => input
                .Description("Language for the battle and the final verdict.")
                .DefaultValue("Russian"))
            .OptionalText("final_summary_focus", "Final Summary Focus", input => input
                .Description("Extra emphasis for the final referee summary.")
                .DefaultValue("Quote the strongest lines, rhetorical turns, reversals, unresolved tensions, and the most memorable formulations."))
            .Agent("host", agent => agent
                .Role("Battle host and final referee")
                .Summary("Opens the battle, injects sharper topics when the exchange goes stale, decides when enough ground has been covered, and delivers the final detailed verdict with quotes.")
                .UseDraft(
                    AgentDefinitionBuilder
                        .New("Philosopher Battle Host", "host")
                        .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, binding => binding
                            .Enabled()
                            .OnlyTools(
                                "session_get",
                                "session_get_parameter",
                                "session_set_parameter",
                                "session_list_turns",
                                "session_append_turn",
                                "session_save_summary",
                                "session_set_phase"))
                        .WithInstructions("""
                            You are the host and referee of a philosophical battle between Kant and Nietzsche.
                            You are not a passive moderator. You are responsible for pacing, topic selection, escalation, and the final verdict.

                            Required workflow inputs:
                            - opening_topic
                            - themes_to_cover
                            - battle_language
                            - final_summary_focus

                            Operating rules:
                            - Read the shared session at the start of every host turn with session_get, session_get_parameter, and session_list_turns.
                            - Use battle_language for every host message and for the final verdict.
                            - Open Topic 1 using opening_topic only when current_topic_number is absent. Frame it sharply in 2-4 sentences, then hand off to one philosopher.
                            - If current_topic_number already exists, do not reopen the same topic. Either continue the current one, redirect it sharply, switch to the next topic, or conclude.
                            - Let the philosophers answer each other directly. They should not wait for the user.
                            - Do not give the floor back to the same philosopher who spoke most recently unless the other philosopher has already spoken since then.
                            - Do not switch topics before both philosophers have addressed the current topic, unless one of them explicitly says the topic is exhausted or sterile.
                            - When the exchange becomes repetitive, vague, polite to the point of dullness, or trapped in abstraction, interrupt decisively.
                            - On intervention, announce why the current thread is exhausted, then inject a sharper new topic or a harder reformulation.
                            - Track progress in session parameters. At minimum keep current_topic_number and current_topic_label updated, and increment them only when you truly move to a new topic.
                            - Cover no more than themes_to_cover distinct topics before the final verdict.
                            - Use session_append_turn for compact host notes only when they will help the final verdict.
                            - When the planned number of themes has been covered, or the battle has clearly peaked, end it yourself.
                            - The final verdict must be long, specific, and quote the transcript with speaker attribution.
                            - In the final verdict, include:
                              1. topic-by-topic recap,
                              2. exact quoted lines with speaker names,
                              3. strongest rhetorical moves,
                              4. best formulations and metaphors,
                              5. conceptual breakthroughs,
                              6. evasions, weak turns, or repetitions,
                              7. unresolved tensions,
                              8. a referee conclusion on who shaped the debate more powerfully.
                            - Save the final verdict via session_save_summary with label final.
                            - Set phase to complete via session_set_phase when the battle is finished.
                            - After the final verdict, do not restart the battle.

                            Keep the energy high, the topics sharp, and the closing verdict unusually detailed.
                            """)
                        .AutoSelectTools(0)
                        .BuildDescription())
                .Capability("task-session-store", "Task session store", capability => capability
                    .Purpose("Read workflow inputs, inspect transcript state, track topic progression, and persist the final verdict.")
                    .Availability(AgentWorkflowCapabilityAvailability.Available)
                    .AvailabilityNote("A generic task session MCP server is available for shared workflow state.")))
            .AgentFromSaved("Immanuel Kant", agent => agent
                .Id("kant")
                .Role("Kantian philosopher")
                .MaxTurnsPerSession(6)
                .MinAssistantTurnsBetweenTurns(2)
                .AppendInstructions("""
                    Workflow mode:
                    - This is an autonomous debate against Nietzsche moderated by the host.
                    - Speak to Nietzsche or the host, not to the user.
                    - Do not ask the user for input or summarize the whole debate.
                    - If the topic is exhausted, say so briefly and push for a sharper reformulation.
                    """))
            .AgentFromSaved("Friedrich Nietzsche", agent => agent
                .Id("nietzsche")
                .Role("Nietzschean philosopher")
                .MaxTurnsPerSession(6)
                .MinAssistantTurnsBetweenTurns(2)
                .AppendInstructions("""
                    Workflow mode:
                    - This is an autonomous debate against Kant moderated by the host.
                    - Speak to Kant or the host, not to the user.
                    - Do not ask the user for input or summarize the whole debate.
                    - If the topic is exhausted, say so brutally and force a sharper reformulation.
                    """))
            .UseHandoff(handoff => handoff
                .StartWith("host")
                .Handoff("host", "kant", "open with Kant")
                .Handoff("host", "nietzsche", "open with Nietzsche")
                .Handoff("kant", "nietzsche", "direct rebuttal")
                .Handoff("nietzsche", "kant", "direct rebuttal")
                .Handoff("kant", "host", "host intervention or conclusion")
                .Handoff("nietzsche", "host", "host intervention or conclusion"))
            .Build();

        workflow
        """";

    public static string ResearchBriefSequential { get; } =
        """"
        var workflow = WorkflowDefinitionBuilder
            .New("research-brief-sequential", "Research Brief Sequential")
            .Description("Three-stage sequential workflow that turns a raw user request into a concise research brief.")
            .RunInteractively()
            .Agent("researcher", agent => agent
                .Role("Researcher")
                .Summary("Extracts the core question, assumptions, and missing evidence.")
                .UseDraft(
                    AgentDefinitionBuilder
                        .New("Sequential Researcher", "researcher")
                        .WithInstructions("""
                            You are the first stage in a sequential workflow.
                            Read the user's request and produce a compact research outline with assumptions, evidence needed, and key unknowns.
                            """)
                        .AutoSelectTools(0)
                        .BuildDescription()))
            .Agent("critic", agent => agent
                .Role("Critical reviewer")
                .Summary("Finds weak reasoning, gaps, and risks in the outline.")
                .UseDraft(
                    AgentDefinitionBuilder
                        .New("Sequential Critic", "critic")
                        .WithInstructions("""
                            You are the second stage in a sequential workflow.
                            Review the prior output critically. Highlight unsupported claims, risks, and missing tradeoffs.
                            """)
                        .AutoSelectTools(0)
                        .BuildDescription()))
            .Agent("writer", agent => agent
                .Role("Brief writer")
                .Summary("Produces the final concise brief for the user.")
                .UseDraft(
                    AgentDefinitionBuilder
                        .New("Sequential Writer", "writer")
                        .WithInstructions("""
                            You are the final stage in a sequential workflow.
                            Turn the prior outputs into a crisp, user-facing brief with clear recommendations and caveats.
                            """)
                        .AutoSelectTools(0)
                        .BuildDescription()))
            .UseSequential(sequential => sequential
                .Order("researcher", "critic", "writer"))
            .Build();

        workflow
        """";

    public static string ProposalPanelConcurrent { get; } =
        """"
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
                        .WithInstructions("""
                            Review the user's proposal from an optimistic angle.
                            Focus on upside, leverage, growth potential, and what could work unusually well.
                            """)
                        .AutoSelectTools(0)
                        .BuildDescription()))
            .Agent("skeptic", agent => agent
                .Role("Skeptical reviewer")
                .Summary("Looks for hidden risk, weak assumptions, and failure modes.")
                .UseDraft(
                    AgentDefinitionBuilder
                        .New("Skeptical Reviewer", "skeptic")
                        .WithInstructions("""
                            Review the user's proposal from a skeptical angle.
                            Focus on weak assumptions, execution risk, hidden cost, and likely failure modes.
                            """)
                        .AutoSelectTools(0)
                        .BuildDescription()))
            .Agent("operator", agent => agent
                .Role("Operational reviewer")
                .Summary("Focuses on execution sequence, dependencies, and decision checkpoints.")
                .UseDraft(
                    AgentDefinitionBuilder
                        .New("Operational Reviewer", "operator")
                        .WithInstructions("""
                            Review the user's proposal from an execution angle.
                            Focus on sequencing, dependencies, rollout checkpoints, and practical next steps.
                            """)
                        .AutoSelectTools(0)
                        .BuildDescription()))
            .UseConcurrent(concurrent => concurrent
                .Participants("optimist", "skeptic", "operator")
                .ConcatenateAllMessages())
            .Build();

        workflow
        """";

    private static IReadOnlyList<WorkflowStarterTemplate> CreateStarterTemplates() =>
    [
        new(
            "philosopher-battle-group-chat",
            "Philosopher Battle Group Chat",
            WorkflowDefinitionKinds.GroupChat,
            PhilosopherBattleGroupChat),
        new(
            "interview-coach-fixed-handoff",
            "Interview Coach Handoff",
            WorkflowDefinitionKinds.Handoff,
            InterviewCoachHandoff),
        new(
            "research-brief-sequential",
            "Research Brief Sequential",
            WorkflowDefinitionKinds.Sequential,
            ResearchBriefSequential),
        new(
            "proposal-panel-concurrent",
            "Proposal Panel Concurrent",
            WorkflowDefinitionKinds.Concurrent,
            ProposalPanelConcurrent)
    ];
}
