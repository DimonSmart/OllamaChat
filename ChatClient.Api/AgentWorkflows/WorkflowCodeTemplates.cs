namespace ChatClient.Api.AgentWorkflows;

public static class WorkflowCodeTemplates
{
    public static string InterviewCoachHandoff { get; } =
        """"
        var workflow = HandoffWorkflowDefinitionBuilder
            .New("interview-coach-fixed-handoff", "Interview Coach Handoff")
            .Description("Specialized conversational flow with one entry router, sequential specialists, explicit fallback edges to triage, and required start inputs collected before the workflow begins.")
            .RequireDocument("resume", "Resume", input => input
                .Description("Candidate resume in markdown format."))
            .RequireDocument("job_description", "Job Description", input => input
                .Description("Target job description in markdown format."))
            .StartWith("triage")
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
            .Fallback("summarizer", "triage", "post-summary fallback")
            .Build();

        workflow
        """";
}
