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

        var triage = CreateTriageAgent();
        var receptionist = CreateReceptionistAgent();
        var behavioural = CreateBehaviouralAgent();
        var technical = CreateTechnicalAgent();
        var summarizer = CreateSummarizerAgent();

        return new AgentWorkflowTemplate
        {
            Id = InterviewCoachWorkflowId,
            DisplayName = "Interview Coach Handoff",
            Description = "Fixed 5-agent handoff workflow modeled after the Microsoft Interview Coach sample: triage -> receptionist -> behavioural -> technical -> summarizer with fallback to triage.",
            Workflow = new AgentWorkflowDefinition
            {
                Id = InterviewCoachWorkflowId,
                DisplayName = "Interview Coach Handoff",
                Description = "Specialized conversational flow with one entry router, sequential specialists, and explicit fallback edges to triage.",
                StartAgentId = TriageAgentId,
                Agents =
                [
                    new AgentWorkflowAgentDefinition
                    {
                        Id = TriageAgentId,
                        Role = "Router / entry point",
                        Summary = "Owns the first turn, reads the shared session state when needed, decides which specialist should take over next, and receives fallback handoffs when the user goes off script.",
                        AgentDraft = triage,
                        CapabilityRequirements =
                        [
                            CreateCapabilityRequirement(
                                "task-session-store",
                                "Task session store",
                                "Read-only access to current phase and stored documents so routing stays deterministic across turns.",
                                capabilityAvailability.TaskSessionStoreAvailability,
                                capabilityAvailability.TaskSessionStoreNote)
                        ]
                    },
                    new AgentWorkflowAgentDefinition
                    {
                        Id = ReceptionistAgentId,
                        Role = "Document intake and session setup",
                        Summary = "Creates the interview context, gathers resume and job description, and decides when the interview is ready to start.",
                        AgentDraft = receptionist,
                        CapabilityRequirements =
                        [
                            CreateCapabilityRequirement(
                                "document-intake",
                                "Document intake",
                                "Resume and job description acquisition/parsing for the receptionist workflow.",
                                capabilityAvailability.DocumentIntakeAvailability,
                                capabilityAvailability.DocumentIntakeNote),
                            CreateCapabilityRequirement(
                                "task-session-store",
                                "Task session store",
                                "Persistent shared state for documents, transcript turns, phase, and summary.",
                                capabilityAvailability.TaskSessionStoreAvailability,
                                capabilityAvailability.TaskSessionStoreNote)
                        ]
                    },
                    new AgentWorkflowAgentDefinition
                    {
                        Id = BehaviouralAgentId,
                        Role = "Behavioural interviewer",
                        Summary = "Runs the behavioural phase, keeps transcript state, and hands over when the user is ready for technical questions.",
                        AgentDraft = behavioural,
                        CapabilityRequirements =
                        [
                            CreateCapabilityRequirement(
                                "task-session-store",
                                "Task session store",
                                "Persistent transcript and phase state across turns.",
                                capabilityAvailability.TaskSessionStoreAvailability,
                                capabilityAvailability.TaskSessionStoreNote)
                        ]
                    },
                    new AgentWorkflowAgentDefinition
                    {
                        Id = TechnicalAgentId,
                        Role = "Technical interviewer",
                        Summary = "Runs role-specific technical questions, appends transcript state, and hands over to the summarizer.",
                        AgentDraft = technical,
                        CapabilityRequirements =
                        [
                            CreateCapabilityRequirement(
                                "task-session-store",
                                "Task session store",
                                "Persistent transcript and phase state across turns.",
                                capabilityAvailability.TaskSessionStoreAvailability,
                                capabilityAvailability.TaskSessionStoreNote)
                        ]
                    },
                    new AgentWorkflowAgentDefinition
                    {
                        Id = SummarizerAgentId,
                        Role = "Wrap-up and summary",
                        Summary = "Builds the final interview summary, marks the interview complete, and can return control to triage for a new request.",
                        AgentDraft = summarizer,
                        CapabilityRequirements =
                        [
                            CreateCapabilityRequirement(
                                "task-session-store",
                                "Task session store",
                                "Read/write access to the transcript and final summary.",
                                capabilityAvailability.TaskSessionStoreAvailability,
                                capabilityAvailability.TaskSessionStoreNote)
                        ]
                    }
                ],
                Handoffs =
                [
                    CreateHandoff(TriageAgentId, ReceptionistAgentId, "start / intake"),
                    CreateHandoff(TriageAgentId, BehaviouralAgentId, "resume interview"),
                    CreateHandoff(TriageAgentId, TechnicalAgentId, "resume technical"),
                    CreateHandoff(TriageAgentId, SummarizerAgentId, "wrap up"),
                    CreateHandoff(ReceptionistAgentId, BehaviouralAgentId, "handoff after intake"),
                    CreateHandoff(ReceptionistAgentId, TriageAgentId, "fallback", isFallback: true),
                    CreateHandoff(BehaviouralAgentId, TechnicalAgentId, "handoff after behavioural"),
                    CreateHandoff(BehaviouralAgentId, TriageAgentId, "fallback", isFallback: true),
                    CreateHandoff(TechnicalAgentId, SummarizerAgentId, "handoff after technical"),
                    CreateHandoff(TechnicalAgentId, TriageAgentId, "fallback", isFallback: true),
                    CreateHandoff(SummarizerAgentId, TriageAgentId, "post-summary fallback", isFallback: true)
                ]
            },
            Assessment = CreateAssessment(capabilityAvailability)
        };
    }

    private static AgentWorkflowCapabilityRequirement CreateCapabilityRequirement(
        string key,
        string displayName,
        string purpose,
        AgentWorkflowCapabilityAvailability availability,
        string note)
    {
        return new AgentWorkflowCapabilityRequirement
        {
            Key = key,
            DisplayName = displayName,
            Purpose = purpose,
            Availability = availability,
            AvailabilityNote = note
        };
    }

    private static AgentWorkflowHandoffDefinition CreateHandoff(
        string fromAgentId,
        string toAgentId,
        string label,
        bool isFallback = false)
    {
        return new AgentWorkflowHandoffDefinition
        {
            FromAgentId = fromAgentId,
            ToAgentId = toAgentId,
            Label = label,
            IsFallback = isFallback
        };
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
                    : "The remaining missing piece is an official handoff runtime wired into the UI session flow."
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
                1. Receptionist: gather resume and job description.
                2. Behavioural interviewer: run behavioural questions.
                3. Technical interviewer: run technical questions.
                4. Summarizer: generate the wrap-up.

                Routing rules:
                - Inspect shared session state with session_get when it is useful.
                - Start with the receptionist when intake is incomplete or no resume / job description is stored.
                - Route to the behavioural interviewer once intake is complete and the interview has not started.
                - Route to the technical interviewer after the behavioural phase is complete.
                - Route to the summarizer when the user wants to stop or both interview phases are complete.
                - If a specialist falls back with an out-of-order request, decide the next best specialist without trying to answer in detail yourself.

                Never conduct the interview yourself.
                Be brief, predictable, and explicit about who is taking over next.
                """)
            .AutoSelectTools(0)
            .BuildDescription();

    private static AgentDescription CreateReceptionistAgent() =>
        AgentDefinitionBuilder
            .New("Interview Coach Receptionist", ReceptionistAgentId)
            .WithBinding(BuiltInTaskSessionMcpServerTools.Descriptor.Name, static binding => binding
                .Enabled()
                .OnlyTools(
                    "session_get",
                    "session_set_phase",
                    "session_attach_document",
                    "session_get_document",
                    "session_append_turn"))
            .WithBinding(BuiltInDocumentIntakeMcpServerTools.Descriptor.Name, static binding => binding
                .Enabled()
                .OnlyTools(
                    "docintake_read_document",
                    "docintake_prepare_markdown"))
            .WithInstructions("""
                You are the receptionist agent for an interview coach workflow.
                Your job is to prepare the interview context before any questioning begins.

                Responsibilities:
                - The shared task session already exists before chat starts. Read it with session_get before deciding whether intake is complete.
                - Ask the user for a resume and a target job description.
                - When the user provides markdown content, normalize it with docintake_prepare_markdown or docintake_read_document and persist it with session_attach_document.
                - Use session_append_turn for concise intake notes or decisions when useful, not as a duplicate log of every utterance.
                - Set the workflow phase with session_set_phase when intake becomes complete.
                - Handoff to the behavioural interviewer when both resume and job_description documents are present and the user is ready.
                - Fallback to triage only for unexpected requests outside the intake phase.

                Do not conduct the interview yourself. Do not summarize the whole session. Stay in the intake role.
                """)
            .AutoSelectTools(0)
            .BuildDescription();

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
