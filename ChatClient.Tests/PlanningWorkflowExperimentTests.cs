using System.ClientModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.GroupChat;
using ChatClient.Api.AgentWorkflows.Runtime;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Tools;
using ChatClient.Api.Services;
using ChatClient.Application.Services.Agentic;
#pragma warning disable MAAI001
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
#pragma warning restore MAAI001
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using Xunit.Abstractions;

namespace ChatClient.Tests;

public sealed class PlanningWorkflowExperimentTests(ITestOutputHelper output)
{
    private const string DevModel = "gpt-oss:120b-cloud";
    private const int DefaultRunCount = 3;
    private const string RunCountEnvironmentVariable = "CHATCLIENT_PLANNING_WORKFLOW_EXPERIMENT_RUNS";

    [RealWorkflowFact]
    [Trait("Category", "RealWorkflowExploration")]
    public async Task SequentialPlanningWorkflow_WithRobotVacuumQuery_CapturesStabilityArtifacts()
    {
        const string scenarioId = "planning-workflow-robot-vacuum-mop-600eur";
        const string userQuery = "Посоветуй хороший робот пылесос с мойкой до 600 EUR";

        var workflow = CreatePlanningWorkflowDefinition();
        var toolCatalog = CreateRealWebToolCatalog();
        var runCount = ResolveRunCount();
        List<PlanningWorkflowExperimentRunArtifact> runs = [];

        for (var runIndex = 1; runIndex <= runCount; runIndex++)
        {
            var runArtifact = await ExecuteWorkflowRunAsync(
                workflow,
                toolCatalog,
                userQuery,
                runIndex);
            runs.Add(runArtifact);

            output.WriteLine(
                $"Run {runIndex}: status={runArtifact.Status}, validPlan={runArtifact.IsValidPlan}, steps={runArtifact.StepCount?.ToString() ?? "-"}, shape={runArtifact.ShapeSignature ?? "<none>"}");
            if (!string.IsNullOrWhiteSpace(runArtifact.ValidationIssueCode))
            {
                output.WriteLine(
                    $"Run {runIndex} validation issue: {runArtifact.ValidationIssueCode} - {runArtifact.ValidationIssueMessage}");
            }

            if (!string.IsNullOrWhiteSpace(runArtifact.ErrorMessage))
            {
                output.WriteLine($"Run {runIndex} error: {runArtifact.ErrorMessage}");
            }
        }

        var artifact = BuildArtifact(scenarioId, userQuery, workflow, runs);
        var writer = new PlanningWorkflowExperimentArtifactWriter();
        var paths = await writer.SaveAsync(artifact);

        output.WriteLine($"Saved workflow experiment summary: {paths.SummaryPath}");
        output.WriteLine($"Saved workflow experiment transcript: {paths.TranscriptPath}");

        Assert.Equal(runCount, artifact.RunCount);
        Assert.NotEmpty(artifact.Runs);
        Assert.All(artifact.Runs, static run => Assert.False(string.IsNullOrWhiteSpace(run.Status)));
    }

    private static GroupChatWorkflowDefinition CreatePlanningWorkflowDefinition()
    {
        var workflow = WorkflowDefinitionBuilder
            .New("planning-workflow-experiment", "Planning Workflow Experiment")
            .Description("Experimental group-chat workflow for multi-pass JSON plan authoring with pairwise contract revision and targeted validation.")
            .RunInteractively()
            .Agent("analyzer", agent => agent
                .Role("Request analyzer")
                .Summary("Expands and clarifies the user request without creating the executable plan.")
                .UseDraft(
                    AgentTemplateBuilder
                        .New("Planning Analyzer", "analyzer")
                        .WithInstructions(BuildAnalyzerInstructions())
                        .AutoSelectTools(0)
                        .Build()))
            .Agent("outline_drafter", agent => agent
                .Role("Workflow drafter")
                .Summary("Produces a textual workflow draft with coarse steps and textual input/output expectations.")
                .UseDraft(
                    AgentTemplateBuilder
                        .New("Workflow Outline Drafter", "outline_drafter")
                        .WithInstructions(BuildOutlineDrafterInstructions())
                        .AutoSelectTools(0)
                        .Build()))
            .Agent("step_materializer", agent => agent
                .Role("Step materializer")
                .Summary("Turns the textual workflow draft into concrete plan steps in the planning domain model.")
                .UseDraft(
                    AgentTemplateBuilder
                        .New("Step Materializer", "step_materializer")
                        .WithInstructions(BuildStepMaterializerInstructions())
                        .AutoSelectTools(0)
                        .Build()))
            .Agent("contract_reviser", agent => agent
                .Role("Contract reviser")
                .Summary("Repairs one adjacent pair of plan steps per turn and verifies that pair with a targeted validator.")
                .UseDraft(
                    AgentTemplateBuilder
                        .New("Contract Reviser", "contract_reviser")
                        .WithInstructions(BuildContractReviserInstructions())
                        .AutoSelectTools(0)
                        .Build()))
            .Agent("plan_reviewer", agent => agent
                .Role("Plan reviewer")
                .Summary("Runs full-plan validation and applies the smallest remaining correction before finalization.")
                .UseDraft(
                    AgentTemplateBuilder
                        .New("Plan Reviewer", "plan_reviewer")
                        .WithInstructions(BuildPlanReviewerInstructions())
                        .AutoSelectTools(0)
                        .Build()))
            .Agent("finalizer", agent => agent
                .Role("Plan finalizer")
                .Summary("Emits the final executable JSON plan after contract revision and full-plan review.")
                .UseDraft(
                    AgentTemplateBuilder
                        .New("Planning Finalizer", "finalizer")
                        .WithInstructions(BuildFinalizerInstructions())
                        .AutoSelectTools(0)
                        .Build()))
            .UseGroupChat(groupChat => groupChat
                .Participants(
                    "analyzer",
                    "outline_drafter",
                    "step_materializer",
                    "contract_reviser",
                    "plan_reviewer",
                    "finalizer")
                .UseProgrammableManager(manager => manager
                    .MaximumIterations(11)
                    .Program(GroupChatManagerPrograms.PrefixCycleSuffix(
                        prefix: ["analyzer", "outline_drafter", "step_materializer"],
                        cycle: ["contract_reviser"],
                        suffix: ["plan_reviewer", "finalizer"]))))
            .Build();

        return workflow as GroupChatWorkflowDefinition
               ?? throw new InvalidOperationException("Expected a group-chat workflow definition.");
    }

    private async Task<PlanningWorkflowExperimentRunArtifact> ExecuteWorkflowRunAsync(
        GroupChatWorkflowDefinition workflow,
        PlanningToolCatalog toolCatalog,
        string userQuery,
        int runIndex)
    {
        var chatClient = BuildChatClient();
        var planWorkspace = new PlanningWorkflowExperimentPlanWorkspace(toolCatalog.ListTools(), userQuery);
        var runtimeAgentsById = CreateRuntimeAgents(workflow, chatClient, planWorkspace);
        var runtimeWorkflow = BuildRuntimeWorkflow(workflow, runtimeAgentsById);
        var userMessage = BuildWorkflowUserMessage(userQuery, toolCatalog);
        var assistantMessages = new List<ChatMessage>();
        var allEvents = new List<WorkflowEvent>();

        try
        {
            await using var run = await InProcessExecution.OpenStreamingAsync(
                runtimeWorkflow,
                $"planning-workflow-experiment-{Guid.NewGuid():N}");

            var conversationAccepted = await run.TrySendMessageAsync<IEnumerable<ChatMessage>>(
                [new ChatMessage(ChatRole.User, userMessage)]);
            Assert.True(conversationAccepted);

            allEvents.AddRange(await CollectEventsAsync(run));
            assistantMessages.AddRange(ExtractAssistantMessages(allEvents));

            if (assistantMessages.Count == 0)
            {
                var turnAccepted = await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
                Assert.True(turnAccepted);

                allEvents.AddRange(await CollectEventsAsync(run));
                assistantMessages.AddRange(ExtractAssistantMessages(allEvents).Skip(assistantMessages.Count));
            }

            var finalOutput = assistantMessages
                .LastOrDefault(static message => message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.Text))
                ?.Text
                ?? string.Empty;
            var transcript = FormatTranscript(assistantMessages);
            return ValidatePlanWorkspace(
                runIndex,
                finalOutput,
                transcript,
                assistantMessages.Count,
                planWorkspace,
                toolCatalog);
        }
        catch (Exception ex)
        {
            return new PlanningWorkflowExperimentRunArtifact
            {
                RunIndex = runIndex,
                Status = "exception",
                IsValidPlan = false,
                ErrorMessage = ex.Message,
                Transcript = FormatTranscript(assistantMessages),
                FinalOutput = assistantMessages.LastOrDefault(static message => message.Role == ChatRole.Assistant)?.Text ?? string.Empty,
                AssistantMessageCount = assistantMessages.Count
            };
        }
    }

    private static PlanningWorkflowExperimentRunArtifact ValidatePlanWorkspace(
        int runIndex,
        string finalOutput,
        string transcript,
        int assistantMessageCount,
        PlanningWorkflowExperimentPlanWorkspace planWorkspace,
        PlanningToolCatalog toolCatalog)
    {
        var validation = planWorkspace.ValidateExecutablePlan();
        var plan = validation.Plan;
        if (plan.Steps.Count == 0)
        {
            return new PlanningWorkflowExperimentRunArtifact
            {
                RunIndex = runIndex,
                Status = "plan_steps_empty",
                IsValidPlan = false,
                Transcript = transcript,
                FinalOutput = finalOutput,
                AssistantMessageCount = assistantMessageCount
            };
        }

        if (validation.StructuralIssue is not null)
        {
            return new PlanningWorkflowExperimentRunArtifact
            {
                RunIndex = runIndex,
                Status = "invalid_plan",
                IsValidPlan = false,
                ValidationIssueCode = validation.StructuralIssue.Code,
                ValidationIssueMessage = validation.StructuralIssue.Message,
                Transcript = transcript,
                FinalOutput = finalOutput,
                AssistantMessageCount = assistantMessageCount,
                StepCount = plan.Steps.Count,
                ShapeSignature = BuildShapeSignature(plan),
                NormalizedPlanJson = PlanJsonProfiles.SerializeIndented(plan, PlanModelProfile.Draft)
            };
        }

        if (validation.SemanticIssue is not null)
        {
            return new PlanningWorkflowExperimentRunArtifact
            {
                RunIndex = runIndex,
                Status = "semantic_invalid_plan",
                IsValidPlan = false,
                ValidationIssueCode = validation.SemanticIssue.Code,
                ValidationIssueMessage = validation.SemanticIssue.Message,
                Transcript = transcript,
                FinalOutput = finalOutput,
                AssistantMessageCount = assistantMessageCount,
                StepCount = plan.Steps.Count,
                ShapeSignature = BuildShapeSignature(plan),
                NormalizedPlanJson = PlanJsonProfiles.SerializeIndented(plan, PlanModelProfile.Draft)
            };
        }

        return new PlanningWorkflowExperimentRunArtifact
        {
            RunIndex = runIndex,
            Status = "valid_plan",
            IsValidPlan = true,
            Transcript = transcript,
            FinalOutput = finalOutput,
            AssistantMessageCount = assistantMessageCount,
            StepCount = plan.Steps.Count,
            ShapeSignature = BuildShapeSignature(plan),
            NormalizedPlanJson = PlanJsonProfiles.SerializeIndented(plan, PlanModelProfile.Draft)
        };
    }

    private static string BuildWorkflowUserMessage(string userQuery, PlanningToolCatalog toolCatalog)
    {
        var sb = new StringBuilder();
        sb.AppendLine("This is an experimental multi-agent planning workflow.");
        sb.AppendLine("The workflow is planning only. It must not execute tools.");
        sb.AppendLine("The executable plan lives in a shared internal plan workspace, not in chat messages.");
        sb.AppendLine();
        sb.AppendLine("Original user request:");
        sb.AppendLine(userQuery.Trim());
        sb.AppendLine();
        sb.AppendLine("Shared planning rules:");
        sb.AppendLine("- Use only the listed external capabilities when designing executable steps.");
        sb.AppendLine("- Build the shortest correct executable plan that can actually satisfy the user request.");
        sb.AppendLine("- Think in user-visible deliverables first.");
        sb.AppendLine("- Make the expected end result explicit and keep every downstream planning decision aligned with that expected result.");
        sb.AppendLine("- Map every required external fact or external action to one listed external capability before adding execution steps.");
        sb.AppendLine("- Do not invent tools, sources, entities, product facts, prices, ratings, or capabilities.");
        sb.AppendLine("- Do not add new user requirements that the user did not ask for.");
        sb.AppendLine("- Do not require fields that the listed capabilities do not clearly return or verify.");
        sb.AppendLine("- LLM steps may transform, normalize, deduplicate, compare, rank, summarize, or validate only the evidence already present in their inputs.");
        sb.AppendLine("- Separate discovery/acquisition from normalization and final synthesis when the user needs exact facts.");
        sb.AppendLine("- Do not treat titles, snippets, ids, or rankings as full source content when exact facts are needed.");
        sb.AppendLine("- If one tool returns records directly compatible with another tool input, preserve those full records instead of collapsing them too early.");
        sb.AppendLine("- If the listed capabilities truly cannot satisfy the request, prefer the shortest blocked plan.");
        sb.AppendLine();
        sb.AppendLine("Tool-set separation:");
        sb.AppendLine("- The listed external capabilities below are NOT callable by workflow agents. They are planning targets only.");
        sb.AppendLine("- Internal plan-workspace tools are separate and are exposed only to the agents that build or revise the plan.");
        sb.AppendLine("- No workflow agent should output the executable plan as chat JSON. The plan must be created and edited only through internal plan-workspace tools.");
        sb.AppendLine();
        sb.AppendLine("Workflow protocol:");
        sb.AppendLine("- analyzer writes a plain-text expanded brief.");
        sb.AppendLine("- outline_drafter writes a plain-text coarse workflow outline.");
        sb.AppendLine("- step_materializer must create the executable plan inside the shared plan workspace by calling internal plan tools. It should return only a short plain-text status note.");
        sb.AppendLine("- contract_reviser must inspect and repair only one adjacent pair of plan steps per turn by calling internal plan tools. It should validate the repaired pair with 'plan_validate_pair' and then return only a short plain-text status note.");
        sb.AppendLine("- plan_reviewer must validate the whole current plan with 'plan_validate_full', apply the smallest remaining fix through internal plan tools, and return only a short plain-text status note.");
        sb.AppendLine("- finalizer returns only a short plain-text completion note. The final plan is read from the shared plan workspace after the workflow run ends.");
        sb.AppendLine("- A plan is not complete unless exactly one step is explicitly marked as the result step.");
        sb.AppendLine();
        sb.AppendLine("Available internal domain operations:");
        sb.AppendLine("- `plan_read_structure` reads the current high-level workflow structure.");
        sb.AppendLine("- `plan_set_goal` sets the workflow goal.");
        sb.AppendLine("- `plan_add_search_step` adds a search step.");
        sb.AppendLine("- `plan_add_download_step` adds a download step. If there is one obvious compatible prior source, the code auto-wires it. If there are several competing branches, the tool will fail instead of guessing.");
        sb.AppendLine("- `plan_add_prepare_download_inputs_step` inserts an LLM bridge that prepares records for a later download step. Without an explicit source it aggregates the terminal compatible prior branches.");
        sb.AppendLine("- `plan_add_extract_step`, `plan_add_filter_step`, `plan_add_rank_step`, and `plan_add_answer_step` add high-level LLM steps and may aggregate terminal compatible prior branches automatically when sourceStepId is omitted.");
        sb.AppendLine("- `plan_mark_result_step` explicitly marks one existing step as the workflow result step.");
        sb.AppendLine("- `plan_autowire_step` recomputes the safest current upstream wiring for one existing step.");
        sb.AppendLine("- `plan_read_pair`, `plan_reconnect_step`, `plan_update_step_instruction`, `plan_delete_step`, and `plan_move_step_after` are for localized repair.");
        sb.AppendLine("- `plan_validate_pair` validates one local adjacency. `plan_validate_full` validates both structural correctness and semantic completion of the whole plan, including presence of an explicit result step.");
        sb.AppendLine();
        PlanningCapabilityPromptFormatter.AppendAgents(sb, []);
        sb.AppendLine();
        PlanningCapabilityPromptFormatter.AppendTools(sb, toolCatalog.ListTools());
        return sb.ToString().Trim();
    }

    private static string BuildAnalyzerInstructions() =>
        """
        You are the first stage in an experimental planning workflow.
        Produce a faithful expanded textual analysis of the user request.

        Rules:
        - Return plain text only. No JSON. No markdown fences.
        - Do not invent external facts, tools, or new user requirements.
        - Keep the brief faithful to the original request.
        - Do not create plan steps and do not mention concrete tool ids as obligations.
        - Describe what the user wants, what constraints matter, and what evidence will be needed.
        - Make the expected end result explicit: what the finished result must contain, what must be true for it to count as successful, and what must definitely not be omitted.
        - State explicitly what artifact should be treated as the workflow result.
        - Use short labeled sections: Expanded Request, Goal, Deliverables, Result Expectations, Result Artifact, Constraints, Evidence Needs, Reasoning Needs, Notes For Workflow Draft.
        """;

    private static string BuildOutlineDrafterInstructions() =>
        """
        You are the second stage in an experimental planning workflow.
        Read the original request, the analyzer output, and the shared planning rules from the conversation.
        Produce a textual workflow draft, not the executable JSON plan.

        Rules:
        - Return plain text only. No JSON. No markdown fences.
        - The workflow draft must be coarse and textual.
        - Do not add new user requirements.
        - Do not require unsupported fields or capabilities.
        - Preserve the analyzer's Result Expectations explicitly, so later planning can keep aiming at the same end result.
        - Preserve the analyzer's Result Artifact explicitly, so later planning knows which concrete step must be marked as the workflow result.
        - Write a short workflow goal followed by numbered coarse steps.
        - Each step should describe purpose, expected input, and expected output in plain language.
        - End with a short Result Step note that says which coarse step should later be marked as the explicit workflow result.
        - End with a short Expected Result reminder that states what the final executable workflow must ultimately produce for the user.
        """;

    private static string BuildStepMaterializerInstructions() =>
        """
        You are the third stage in an experimental planning workflow.
        Read the original request, the analyzer brief, the textual workflow draft, and the shared planning rules.
        Create the executable plan inside the shared internal plan workspace by calling plan-workspace tools.

        Rules:
        - Do NOT output the plan as JSON in chat.
        - You MUST create the plan by calling internal plan-workspace tools.
        - First inspect the current workspace with plan_read_structure. Then set the plan goal with plan_set_goal.
        - Read the analyzer and outline carefully enough to keep the expected result explicit in your reasoning.
        - You MUST explicitly mark one existing step as the workflow result by calling plan_mark_result_step.
        - Build the workflow with high-level domain operations such as plan_add_search_step, plan_add_download_step, plan_add_extract_step, plan_add_filter_step, plan_add_rank_step, and plan_add_answer_step.
        - Omit sourceStepId whenever the intended upstream source is obvious. Let the code handle safe default wiring instead of micro-managing bindings.
        - If several search-like branches should feed one later download, first add one plan_add_prepare_download_inputs_step without an explicit source so the code aggregates those branches, and only then add plan_add_download_step.
        - If a download step must follow an LLM-produced record set, first add plan_add_prepare_download_inputs_step and only then add plan_add_download_step.
        - Materialize the textual workflow into the shortest correct concrete steps.
        - The plan is not complete unless exactly one step is explicitly marked as the result step and that step really represents the expected final result.
        - The workflow you build must actually lead to the expected result stated by the analyzer and outline, not merely to intermediate extracted data.
        - Do not add new user requirements.
        - Use only the listed external capabilities as planning targets inside executable steps.
        - Do not require fields that the tools do not clearly return or verify.
        - Prefer preserving compatible records over lossy projections.
        - Let the code handle low-level step JSON, prompt scaffolding, bindings, and tool-argument wiring.
        - You MUST call plan_validate_full before you finish.
        - When you are done, return one short plain-text note like 'Draft plan materialized.'
        """;

    private static string BuildContractReviserInstructions() =>
        """
        You are a contract reviser in a cyclic planning workflow.
        Repair only one adjacent pair of plan steps per turn inside the shared internal plan workspace, then verify that pair with the validator tool.

        How to choose the pair:
        - Count previous assistant messages authored by contract_reviser in the conversation.
        - Let pairIndex = previousContractReviserMessageCount.
        - Read that pair from the shared plan workspace with plan_read_pair(pairIndex).
        - If that adjacent pair does not exist, return one short plain-text note saying no more pairwise revisions are needed.

        Fix only the selected pair. Focus on:
        - required input names,
        - map vs value usage,
        - array vs object compatibility,
        - lossy or incompatible projections,
        - assumptions about outputs that are not guaranteed,
        - prompt placeholders or missing prompts when they break the pair,
        - literal helper wrappers like {"value":...} when a tool expects a raw literal.

        Rules:
        - You MUST inspect the current plan workspace before editing with plan_read_pair or plan_read_step.
        - Prefer plan_autowire_step as the first repair tool when a downstream step is attached to the wrong upstream evidence.
        - Edit the current plan only through internal high-level tools such as plan_autowire_step, plan_reconnect_step, plan_move_step_after, plan_delete_step, plan_add_download_step, or plan_add_prepare_download_inputs_step.
        - Fix only what is needed to resolve the selected pairwise contract issue.
        - Do not add unrelated steps while fixing one pairwise contract issue.
        - After you make the minimal repair, you MUST call the local tool plan_validate_pair with the exact fromStepId and toStepId for the pair you repaired.
        - If the validator says ok=false, keep repairing the same pair and call the tool again before you answer.
        - When the validator says ok=true, return one short plain-text note describing what pair was checked or fixed.
        """;

    private static string BuildPlanReviewerInstructions() =>
        """
        You are the full-plan reviewer in an experimental planning workflow.
        Review the current shared plan workspace as a whole after the pairwise contract cycle.

        Rules:
        - You MUST inspect the current plan workspace before answering with plan_read_structure or plan_read_step.
        - You MUST call the local tool plan_validate_full before answering.
        - The validator now checks semantic completion too. A plan is not valid if it does not explicitly mark one result step or if that result step does not reach the deliverable.
        - Keep the analyzer's and outline's expected result in mind. A structurally valid workflow is still wrong if it stops before the user-visible result.
        - If the validator returns ok=true, return one short plain-text note saying the plan is valid.
        - If the validator returns ok=false, make the smallest concrete fix that resolves the reported issue through high-level internal tools such as plan_mark_result_step, plan_autowire_step, plan_reconnect_step, plan_move_step_after, plan_delete_step, plan_add_download_step, plan_add_prepare_download_inputs_step, plan_add_extract_step, plan_add_filter_step, plan_add_rank_step, or plan_add_answer_step.
        - After a fix, call plan_validate_full again before answering.
        - Do not add speculative steps and do not rewrite the whole plan unless the validator issue clearly requires it.
        - Return only a short plain-text review note.
        """;

    private static string BuildFinalizerInstructions() =>
        """
        You are the final stage in an experimental planning workflow.
        Use the original request, the analyzer brief, the textual workflow draft, the contract-revision notes, the full-plan review note, and the shared planning rules from the conversation.

        Rules:
        - Return only a short plain-text completion note. Do not output the plan JSON.
        - Do not edit the plan.
        - Do not add new user requirements.
        - Keep the note short.
        """;

    private static IReadOnlyDictionary<string, AIAgent> CreateRuntimeAgents(
        IOrchestrationWorkflowDefinition workflow,
        IChatClient chatClient,
        PlanningWorkflowExperimentPlanWorkspace planWorkspace)
    {
        Dictionary<string, AIAgent> agentsById = new(StringComparer.OrdinalIgnoreCase);
        var serviceProvider = new ServiceCollection().BuildServiceProvider();

        foreach (var agent in workflow.Agents)
        {
            var draft = agent.AgentDraft
                        ?? throw new InvalidOperationException(
                            $"Workflow agent '{agent.Id}' does not have a draft.");

            var tools = agent.Id switch
            {
                "step_materializer" => planWorkspace.CreateMaterializerTools(),
                "contract_reviser" => planWorkspace.CreateContractReviserTools(),
                "plan_reviewer" => planWorkspace.CreatePlanReviewerTools(),
                _ => []
            };

            if (tools.Count == 0)
            {
                agentsById[agent.Id] = new ChatClientAgent(
                    chatClient,
                    draft.Content ?? string.Empty,
                    draft.AgentName ?? agent.Id,
                    null,
                    null,
                    null,
                    null);
                continue;
            }

            agentsById[agent.Id] = new ChatClientAgent(
                chatClient,
                new ChatClientAgentOptions
                {
                    Id = agent.Id,
                    Name = draft.AgentName ?? agent.Id,
                    ChatOptions = new ChatOptions
                    {
                        Instructions = draft.Content ?? string.Empty,
                        Tools = [.. tools],
                        ToolMode = ChatToolMode.RequireAny,
                        AllowMultipleToolCalls = true
                    },
                    UseProvidedChatClientAsIs = false
                },
                NullLoggerFactory.Instance,
                serviceProvider);
        }

        return agentsById;
    }

    private static Workflow BuildRuntimeWorkflow(
        IOrchestrationWorkflowDefinition workflow,
        IReadOnlyDictionary<string, AIAgent> runtimeAgentsById)
    {
        return workflow switch
        {
            GroupChatWorkflowDefinition groupChat => new GroupChatRuntimeWorkflowBuilder(new GroupChatManagerRegistry([])).Build(
                groupChat,
                runtimeAgentsById,
                new OrchestrationRuntimeBuildContext()),
            SequentialWorkflowDefinition sequential => new SequentialRuntimeWorkflowBuilder().Build(
                sequential,
                runtimeAgentsById,
                new OrchestrationRuntimeBuildContext()),
            _ => throw new InvalidOperationException(
                $"Unsupported experimental workflow kind '{workflow.Kind}'.")
        };
    }

    private static async Task<List<WorkflowEvent>> CollectEventsAsync(StreamingRun run)
    {
        List<WorkflowEvent> workflowEvents = [];

        await foreach (var workflowEvent in run.WatchStreamAsync())
        {
            workflowEvents.Add(workflowEvent);
        }

        return workflowEvents;
    }

    private static IReadOnlyList<ChatMessage> ExtractAssistantMessages(IEnumerable<WorkflowEvent> workflowEvents)
    {
        List<ChatMessage> assistantMessages = [];

        foreach (var outputEvent in workflowEvents.OfType<WorkflowOutputEvent>())
        {
            foreach (var message in ExtractOutputMessages(outputEvent))
            {
                if (message.Role == ChatRole.Assistant)
                    assistantMessages.Add(message);
            }
        }

        return assistantMessages;
    }

    private static IEnumerable<ChatMessage> ExtractOutputMessages(WorkflowOutputEvent outputEvent)
    {
        if (outputEvent.Is<List<ChatMessage>>(out var listMessages) && listMessages is not null)
            return listMessages;

        if (outputEvent.Is<IReadOnlyList<ChatMessage>>(out var readOnlyMessages) && readOnlyMessages is not null)
            return readOnlyMessages;

        if (outputEvent.Is<ChatMessage>(out var singleMessage) && singleMessage is not null)
            return [singleMessage];

        return [];
    }

    private static string FormatTranscript(IEnumerable<ChatMessage> assistantMessages)
    {
        var transcriptBuilder = new StringBuilder();

        foreach (var message in assistantMessages)
        {
            var speaker = string.IsNullOrWhiteSpace(message.AuthorName)
                ? "assistant"
                : message.AuthorName!;
            transcriptBuilder.AppendLine($"[{speaker}]");
            transcriptBuilder.AppendLine(message.Text);
            transcriptBuilder.AppendLine();
        }

        return transcriptBuilder.ToString().TrimEnd();
    }

    private static string BuildShapeSignature(PlanDefinition plan) =>
        string.Join(
            " -> ",
            plan.Steps.Select(step =>
                string.IsNullOrWhiteSpace(step.CapabilityId)
                    ? step.Kind
                    : $"{step.Kind}:{step.CapabilityId}"));

    private static PlanningWorkflowExperimentArtifact BuildArtifact(
        string scenarioId,
        string userQuery,
        IOrchestrationWorkflowDefinition workflow,
        IReadOnlyList<PlanningWorkflowExperimentRunArtifact> runs)
    {
        var distinctShapes = runs
            .Where(static run => !string.IsNullOrWhiteSpace(run.ShapeSignature))
            .Select(static run => run.ShapeSignature!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToList();

        return new PlanningWorkflowExperimentArtifact
        {
            ScenarioId = scenarioId,
            UserQuery = userQuery,
            WorkflowId = workflow.Id,
            WorkflowDisplayName = workflow.DisplayName,
            RunCount = runs.Count,
            ValidPlanCount = runs.Count(static run => run.IsValidPlan),
            DistinctShapeCount = distinctShapes.Count,
            DistinctShapes = distinctShapes,
            Runs = runs.ToList()
        };
    }

    private static int ResolveRunCount()
    {
        var rawValue = Environment.GetEnvironmentVariable(RunCountEnvironmentVariable);
        if (int.TryParse(rawValue, out var parsed) && parsed > 0)
            return parsed;

        return DefaultRunCount;
    }

    private static PlanningToolCatalog CreateRealWebToolCatalog()
    {
        var factoryMethod = typeof(PlanningPipelineIntegrationTests).GetMethod(
            "CreateRealWebToolCatalog",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find real web tool catalog factory.");

        return (PlanningToolCatalog)(factoryMethod.Invoke(null, [new TestHttpClientFactory()]) ??
                                     throw new InvalidOperationException("Real web tool catalog factory returned null."));
    }

    private static IChatClient BuildChatClient()
    {
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri("http://localhost:11434/v1/")
        };

        return new OpenAIClient(new ApiKeyCredential("ollama"), clientOptions)
            .GetChatClient(DevModel)
            .AsIChatClient();
    }
}

public sealed class PlanningWorkflowExperimentArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _artifactDirectory = Path.Combine(
        TestPathHelper.FindRepositoryRoot(),
        "artifacts",
        "planning-workflow-experiments");

    public async Task<PlanningWorkflowExperimentArtifactPaths> SaveAsync(
        PlanningWorkflowExperimentArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_artifactDirectory);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var safeName = TestPathHelper.SanitizeFileName(artifact.ScenarioId);
        var filePrefix = Path.Combine(_artifactDirectory, $"{stamp}-{safeName}");
        var summaryPath = $"{filePrefix}.json";
        var transcriptPath = $"{filePrefix}.transcript.txt";

        await File.WriteAllTextAsync(
            summaryPath,
            JsonSerializer.Serialize(artifact, JsonOptions),
            cancellationToken);
        await File.WriteAllTextAsync(
            transcriptPath,
            BuildTranscriptFile(artifact),
            cancellationToken);

        return new PlanningWorkflowExperimentArtifactPaths
        {
            SummaryPath = summaryPath,
            TranscriptPath = transcriptPath
        };
    }

    private static string BuildTranscriptFile(PlanningWorkflowExperimentArtifact artifact)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Scenario: {artifact.ScenarioId}");
        sb.AppendLine($"Workflow: {artifact.WorkflowDisplayName} ({artifact.WorkflowId})");
        sb.AppendLine($"Query: {artifact.UserQuery}");
        sb.AppendLine($"Runs: {artifact.RunCount}");
        sb.AppendLine($"Valid plans: {artifact.ValidPlanCount}");
        sb.AppendLine($"Distinct shapes: {artifact.DistinctShapeCount}");
        sb.AppendLine();

        foreach (var run in artifact.Runs.OrderBy(static run => run.RunIndex))
        {
            sb.AppendLine($"=== Run {run.RunIndex} ===");
            sb.AppendLine($"Status: {run.Status}");
            sb.AppendLine($"Valid plan: {run.IsValidPlan}");
            if (!string.IsNullOrWhiteSpace(run.ShapeSignature))
                sb.AppendLine($"Shape: {run.ShapeSignature}");
            if (!string.IsNullOrWhiteSpace(run.ValidationIssueCode))
                sb.AppendLine($"Validation: {run.ValidationIssueCode} - {run.ValidationIssueMessage}");
            if (!string.IsNullOrWhiteSpace(run.ErrorMessage))
                sb.AppendLine($"Error: {run.ErrorMessage}");

            sb.AppendLine();
            sb.AppendLine(run.Transcript);
            sb.AppendLine();
            sb.AppendLine("--- Final Output ---");
            sb.AppendLine(run.FinalOutput);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}

public sealed class PlanningWorkflowExperimentArtifactPaths
{
    public string SummaryPath { get; init; } = string.Empty;

    public string TranscriptPath { get; init; } = string.Empty;
}

public sealed class PlanningWorkflowExperimentArtifact
{
    public string ScenarioId { get; init; } = string.Empty;

    public string UserQuery { get; init; } = string.Empty;

    public string WorkflowId { get; init; } = string.Empty;

    public string WorkflowDisplayName { get; init; } = string.Empty;

    public int RunCount { get; init; }

    public int ValidPlanCount { get; init; }

    public int DistinctShapeCount { get; init; }

    public List<string> DistinctShapes { get; init; } = [];

    public List<PlanningWorkflowExperimentRunArtifact> Runs { get; init; } = [];
}

public sealed class PlanningWorkflowExperimentRunArtifact
{
    public int RunIndex { get; init; }

    public string Status { get; init; } = string.Empty;

    public bool IsValidPlan { get; init; }

    public string? ErrorMessage { get; init; }

    public string? ValidationIssueCode { get; init; }

    public string? ValidationIssueMessage { get; init; }

    public int AssistantMessageCount { get; init; }

    public int? StepCount { get; init; }

    public string? ShapeSignature { get; init; }

    public string Transcript { get; init; } = string.Empty;

    public string FinalOutput { get; init; } = string.Empty;

    public string? NormalizedPlanJson { get; init; }
}
