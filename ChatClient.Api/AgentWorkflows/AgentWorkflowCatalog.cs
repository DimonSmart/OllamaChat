using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Domain.Models;

namespace ChatClient.Api.AgentWorkflows;

public sealed class AgentWorkflowCatalog(IMcpServerConfigService mcpServerConfigService) : IAgentWorkflowCatalog
{
    private const string InterviewCoachWorkflowId = "interview-coach-fixed-handoff";
    private const string TriageAgentId = "triage";
    private const string ReceptionistAgentId = "receptionist";
    private const string BehaviouralAgentId = "behavioural";
    private const string TechnicalAgentId = "technical";
    private const string SummarizerAgentId = "summarizer";

    public async Task<IReadOnlyList<AgentWorkflowTemplate>> ListAsync(CancellationToken cancellationToken = default)
    {
        var servers = await mcpServerConfigService.GetAllAsync();
        return [CreateInterviewCoachTemplate(servers)];
    }

    public async Task<AgentWorkflowTemplate> GetRequiredAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var templates = await ListAsync(cancellationToken);
        var template = templates.FirstOrDefault(static template =>
            string.Equals(template.Id, InterviewCoachWorkflowId, StringComparison.OrdinalIgnoreCase));

        if (template is not null
            && string.Equals(workflowId, InterviewCoachWorkflowId, StringComparison.OrdinalIgnoreCase))
        {
            return template;
        }

        throw new KeyNotFoundException($"Workflow template '{workflowId}' was not found.");
    }

    private static AgentWorkflowTemplate CreateInterviewCoachTemplate(IReadOnlyCollection<IMcpServerDescriptor> servers)
    {
        var capabilityAvailability = EvaluateCapabilities(servers);

        var startInputs = CreateInterviewCoachStartInputs();
        var triage = CreateTriageAgent();
        var receptionist = CreateReceptionistAgent(startInputs);
        var behavioural = CreateBehaviouralAgent();
        var technical = CreateTechnicalAgent();
        var summarizer = CreateSummarizerAgent();
        var workflow = WorkflowDefinitionBuilder
            .New(InterviewCoachWorkflowId, "Interview Coach Handoff")
            .Description("Specialized conversational flow with one entry router, sequential specialists, explicit fallback edges to triage, and required start inputs collected before the workflow begins.")
            .RequireDocument("resume", "Resume", static input => input
                .Description("Candidate resume in markdown format."))
            .RequireDocument("job_description", "Job Description", static input => input
                .Description("Target job description in markdown format."))
            .Agent(TriageAgentId, agent => agent
                .Role("Router / entry point")
                .Summary("Owns the first turn, reads the shared session state when needed, decides which specialist should take over next, and receives fallback handoffs when the user goes off script.")
                .UseDraft(triage)
                .Capability("task-session-store", "Task session store", capability => capability
                    .Purpose("Read-only access to current phase and stored inputs so routing stays deterministic across turns.")
                    .Availability(capabilityAvailability.TaskSessionStoreAvailability)
                    .AvailabilityNote(capabilityAvailability.TaskSessionStoreNote)))
            .Agent(ReceptionistAgentId, agent => agent
                .Role("Start-input validation and session setup")
                .Summary("Verifies that the required workflow inputs are already attached to the shared session, then marks intake complete and hands over to the behavioural interviewer.")
                .UseDraft(receptionist)
                .Capability("task-session-store", "Task session store", capability => capability
                    .Purpose("Persistent shared state for workflow inputs, transcript turns, phase, and summary.")
                    .Availability(capabilityAvailability.TaskSessionStoreAvailability)
                    .AvailabilityNote(capabilityAvailability.TaskSessionStoreNote)))
            .Agent(BehaviouralAgentId, agent => agent
                .Role("Behavioural interviewer")
                .Summary("Runs the behavioural phase, keeps transcript state, and hands over when the user is ready for technical questions.")
                .UseDraft(behavioural)
                .Capability("task-session-store", "Task session store", capability => capability
                    .Purpose("Persistent transcript and phase state across turns.")
                    .Availability(capabilityAvailability.TaskSessionStoreAvailability)
                    .AvailabilityNote(capabilityAvailability.TaskSessionStoreNote)))
            .Agent(TechnicalAgentId, agent => agent
                .Role("Technical interviewer")
                .Summary("Runs role-specific technical questions, appends transcript state, and hands over to the summarizer.")
                .UseDraft(technical)
                .Capability("task-session-store", "Task session store", capability => capability
                    .Purpose("Persistent transcript and phase state across turns.")
                    .Availability(capabilityAvailability.TaskSessionStoreAvailability)
                    .AvailabilityNote(capabilityAvailability.TaskSessionStoreNote)))
            .Agent(SummarizerAgentId, agent => agent
                .Role("Wrap-up and summary")
                .Summary("Builds the final interview summary, marks the interview complete, and can return control to triage for a new request.")
                .UseDraft(summarizer)
                .Capability("task-session-store", "Task session store", capability => capability
                    .Purpose("Read/write access to the transcript and final summary.")
                    .Availability(capabilityAvailability.TaskSessionStoreAvailability)
                    .AvailabilityNote(capabilityAvailability.TaskSessionStoreNote)))
            .UseHandoff(handoff => handoff
                .StartWith(TriageAgentId)
                .Handoff(TriageAgentId, ReceptionistAgentId, "start / intake")
                .Handoff(TriageAgentId, BehaviouralAgentId, "resume interview")
                .Handoff(TriageAgentId, TechnicalAgentId, "resume technical")
                .Handoff(TriageAgentId, SummarizerAgentId, "wrap up")
                .Handoff(ReceptionistAgentId, BehaviouralAgentId, "handoff after intake")
                .Fallback(ReceptionistAgentId, TriageAgentId)
                .Handoff(BehaviouralAgentId, TechnicalAgentId, "handoff after behavioural")
                .Fallback(BehaviouralAgentId, TriageAgentId)
                .Handoff(TechnicalAgentId, SummarizerAgentId, "handoff after technical")
                .Fallback(TechnicalAgentId, TriageAgentId)
                .Fallback(SummarizerAgentId, TriageAgentId, "post-summary fallback"))
            .Build() as AgentWorkflowDefinition
            ?? throw new InvalidOperationException(
                "Interview coach template must materialize as a handoff workflow.");

        return new AgentWorkflowTemplate
        {
            Id = InterviewCoachWorkflowId,
            DisplayName = "Interview Coach Handoff",
            Description = "Builder-defined 5-agent handoff workflow modeled after the Microsoft Interview Coach sample: triage -> receptionist -> behavioural -> technical -> summarizer with fallback to triage.",
            Workflow = workflow,
            Assessment = CreateAssessment(capabilityAvailability)
        };
    }

    private static IReadOnlyList<WorkflowStartInputDefinition> CreateInterviewCoachStartInputs()
    {
        return
        [
            new WorkflowStartInputDefinition
            {
                Key = "resume",
                DisplayName = "Resume",
                Description = "Candidate resume in markdown format.",
                Kind = WorkflowStartInputKind.MarkdownDocument,
                IsRequired = true
            },
            new WorkflowStartInputDefinition
            {
                Key = "job_description",
                DisplayName = "Job Description",
                Description = "Target job description in markdown format.",
                Kind = WorkflowStartInputKind.MarkdownDocument,
                IsRequired = true
            }
        ];
    }

    private static AgentWorkflowAssessment CreateAssessment(WorkflowCapabilityAvailabilitySummary capabilityAvailability)
    {
        return new AgentWorkflowAssessment
        {
            FluentBuilderIsSufficient = true,
            FluentBuilderReason = "Current fluent builder already supports per-agent naming, prompts, model binding, execution settings, and per-agent MCP bindings. That is enough to materialize the five specialized agents required by the fixed handoff scheme.",
            ExistingSavedAgentsAreReusable = false,
            ExistingSavedAgentsReason = "Current saved agents are generic assistants or domain personas. None map cleanly to triage, receptionist, behavioural interviewer, technical interviewer, or summarizer, so reusing their prompts would be more confusing than creating dedicated workflow agents.",
            ReusableProjectPieces =
            [
                "AgentDefinitionBuilder and AgentDescriptionFactory are already expressive enough for specialized role agents.",
                "Existing MCP binding model already supports per-agent tool scoping when suitable capabilities exist.",
                "Built-in MCP host pattern can be reused for workflow-scoped task state and document intake capabilities.",
                "Existing agent persistence and resolved-model pipeline can be reused for workflow-owned agents."
            ],
            MissingProjectPieces =
            [
                capabilityAvailability.DocumentIntakeAvailability == AgentWorkflowCapabilityAvailability.Missing
                    ? "The project does not currently expose a document intake capability."
                    : capabilityAvailability.DocumentIntakeAvailability == AgentWorkflowCapabilityAvailability.Partial
                        ? "The project only has partial document intake support via markdown-bound documents, not full resume parsing like MarkItDown."
                        : "MarkItDown-style non-markdown conversion is still deferred for a later slice.",
                capabilityAvailability.TaskSessionStoreAvailability == AgentWorkflowCapabilityAvailability.Missing
                    ? "The project does not currently expose a generic task session MCP store for shared workflow state."
                    : "No critical blocking gaps remain for this workflow. Remaining work is mostly broader starter coverage and authoring UX polish."
            ]
        };
    }

    private static AgentDescription CreateTriageAgent() =>
        AgentDefinitionBuilder
            .New("Interview Coach Triage", TriageAgentId)
            .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, static binding => binding
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
            .BuildDescription();

    private static AgentDescription CreateReceptionistAgent(IReadOnlyList<WorkflowStartInputDefinition> startInputs)
    {
        var requiredInputSummary = string.Join(
            Environment.NewLine,
            startInputs
                .Where(static input => input.IsRequired)
                .Select(input => $"- {input.DisplayName} (kind: {input.Key})"));

        return
        AgentDefinitionBuilder
            .New("Interview Coach Receptionist", ReceptionistAgentId)
            .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, static binding => binding
                .Enabled()
                .OnlyTools(
                    "session_get",
                    "session_set_phase",
                    "session_get_document",
                    "session_append_turn"))
            .WithInstructions($$"""
                You are the receptionist agent for an interview coach workflow.
                Your job is to prepare the interview context before any questioning begins.

                Required workflow start inputs:
                {{requiredInputSummary}}

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
            .BuildDescription();
    }

    private static AgentDescription CreateBehaviouralAgent() =>
        AgentDefinitionBuilder
            .New("Interview Coach Behavioural Interviewer", BehaviouralAgentId)
            .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, static binding => binding
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
            .BuildDescription();

    private static AgentDescription CreateTechnicalAgent() =>
        AgentDefinitionBuilder
            .New("Interview Coach Technical Interviewer", TechnicalAgentId)
            .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, static binding => binding
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
            .BuildDescription();

    private static AgentDescription CreateSummarizerAgent() =>
        AgentDefinitionBuilder
            .New("Interview Coach Summarizer", SummarizerAgentId)
            .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, static binding => binding
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
            .BuildDescription();

    private static WorkflowCapabilityAvailabilitySummary EvaluateCapabilities(IReadOnlyCollection<IMcpServerDescriptor> servers)
    {
        var serverNames = servers
            .Select(static server => server.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        var hasDocumentIntake = serverNames.Any(static name =>
            name.Contains("document intake", StringComparison.OrdinalIgnoreCase));
        var hasMarkdownDocument = serverNames.Any(static name =>
            name.Contains("markdown document", StringComparison.OrdinalIgnoreCase));
        var hasTaskSessionStore = serverNames.Any(static name =>
            name.Contains("task session", StringComparison.OrdinalIgnoreCase));

        var documentIntakeAvailability = hasDocumentIntake
            ? AgentWorkflowCapabilityAvailability.Available
            : hasMarkdownDocument
                ? AgentWorkflowCapabilityAvailability.Partial
                : AgentWorkflowCapabilityAvailability.Missing;

        var documentIntakeNote = hasDocumentIntake
            ? "A document intake MCP server is available for receptionist-owned intake. This first slice assumes markdown input and can later be extended with MarkItDown."
            : hasMarkdownDocument
                ? "Only a markdown-bound document reader exists today. That helps with structured markdown files, but it is not equivalent to MarkItDown-style parsing of resume files."
                : "No document parsing or markdown-bound intake capability suitable for receptionist-style resume intake was found.";

        var taskSessionAvailability = hasTaskSessionStore
            ? AgentWorkflowCapabilityAvailability.Available
            : AgentWorkflowCapabilityAvailability.Missing;

        var taskSessionNote = hasTaskSessionStore
            ? "A generic task session MCP server is available for shared workflow state."
            : "No task session MCP server was found. The article's shared session/transcript pattern cannot be reproduced faithfully yet.";

        return new WorkflowCapabilityAvailabilitySummary(
            documentIntakeAvailability,
            documentIntakeNote,
            taskSessionAvailability,
            taskSessionNote);
    }

    private sealed record WorkflowCapabilityAvailabilitySummary(
        AgentWorkflowCapabilityAvailability DocumentIntakeAvailability,
        string DocumentIntakeNote,
        AgentWorkflowCapabilityAvailability TaskSessionStoreAvailability,
        string TaskSessionStoreNote);
}
