using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.Services;
using Microsoft.Extensions.AI;

namespace ChatClient.Tests;

internal sealed partial class PlanningWorkflowExperimentPlanWorkspace(
    IReadOnlyCollection<AppToolDescriptor> workflowTools,
    string? userQuery = null)
{
    private const string ToolReadStructure = "plan_read_structure";
    private const string ToolReadStep = "plan_read_step";
    private const string ToolReadPair = "plan_read_pair";
    private const string ToolSetGoal = "plan_set_goal";
    private const string ToolAddSearchStep = "plan_add_search_step";
    private const string ToolAddDownloadStep = "plan_add_download_step";
    private const string ToolAddPrepareDownloadInputsStep = "plan_add_prepare_download_inputs_step";
    private const string ToolAddExtractStep = "plan_add_extract_step";
    private const string ToolAddFilterStep = "plan_add_filter_step";
    private const string ToolAddRankStep = "plan_add_rank_step";
    private const string ToolAddAnswerStep = "plan_add_answer_step";
    private const string ToolMarkResultStep = "plan_mark_result_step";
    private const string ToolAutowireStep = "plan_autowire_step";
    private const string ToolReconnectStep = "plan_reconnect_step";
    private const string ToolUpdateStepInstruction = "plan_update_step_instruction";
    private const string ToolDeleteStep = "plan_delete_step";
    private const string ToolMoveStepAfter = "plan_move_step_after";
    private const string ToolValidatePair = "plan_validate_pair";
    private const string ToolValidateFull = "plan_validate_full";
    private const string SearchCapabilityId = "built-in-web:search";
    private const string DownloadCapabilityId = "built-in-web:download";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private readonly IReadOnlyCollection<AppToolDescriptor> _workflowTools = workflowTools;
    private readonly Dictionary<string, AppToolDescriptor> _workflowToolsById = workflowTools
        .ToDictionary(tool => tool.QualifiedName, StringComparer.OrdinalIgnoreCase);
    private readonly List<WorkflowStepSpec> _steps = [];
    private readonly string? _userQuery = string.IsNullOrWhiteSpace(userQuery) ? null : userQuery.Trim();
    private string? _resultStepId;
    private string _goal = "Planning workspace goal is not set yet.";

    public PlanDefinition BuildPlan() => CompilePlan(_steps);

    public IReadOnlyList<AITool> CreateMaterializerTools() =>
    [
        AIFunctionFactory.Create((Func<JsonObject>)ReadStructure, ToolReadStructure, "Read the current high-level workflow structure.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)SetGoal, ToolSetGoal, "Set the current workflow goal.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, int?, string?, JsonObject>)AddSearchStep, ToolAddSearchStep, "Add a search step with query and limit.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, string?, JsonObject>)AddDownloadStep, ToolAddDownloadStep, "Add a download step after a compatible source step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, string?, string?, JsonObject>)AddPrepareDownloadInputsStep, ToolAddPrepareDownloadInputsStep, "Add an LLM step that prepares download-compatible records for a later download step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, string?, string?, JsonObject>)AddExtractStep, ToolAddExtractStep, "Add an extraction step that turns source evidence into structured JSON records.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, string?, string?, JsonObject>)AddFilterStep, ToolAddFilterStep, "Add a filtering step that keeps only records matching the criteria.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, string?, string?, JsonObject>)AddRankStep, ToolAddRankStep, "Add a ranking step that orders records by the given criteria.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, string?, string?, string?, JsonObject>)AddAnswerStep, ToolAddAnswerStep, "Add a final answer-writing step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)MarkResultStep, ToolMarkResultStep, "Mark one existing workflow step as the explicit result step.", JsonOptions),
        AIFunctionFactory.Create((Func<JsonObject>)ValidateFull, ToolValidateFull, "Validate the entire compiled executable plan.", JsonOptions)
    ];

    public IReadOnlyList<AITool> CreateContractReviserTools() =>
    [
        AIFunctionFactory.Create((Func<JsonObject>)ReadStructure, ToolReadStructure, "Read the current high-level workflow structure.", JsonOptions),
        AIFunctionFactory.Create((Func<int?, JsonObject>)ReadPair, ToolReadPair, "Read one adjacent pair of workflow steps by pair index.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)ReadStep, ToolReadStep, "Read one workflow step by id.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, JsonObject>)ReconnectStep, ToolReconnectStep, "Reconnect one step to a new single upstream source step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)AutowireStep, ToolAutowireStep, "Recompute the safest upstream source wiring for one existing step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, JsonObject>)UpdateStepInstruction, ToolUpdateStepInstruction, "Update the strategic instruction of one non-tool step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)DeleteStep, ToolDeleteStep, "Delete one workflow step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, JsonObject>)MoveStepAfter, ToolMoveStepAfter, "Move one workflow step after another step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, string?, JsonObject>)AddDownloadStep, ToolAddDownloadStep, "Add a download step after a compatible source step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, string?, string?, JsonObject>)AddPrepareDownloadInputsStep, ToolAddPrepareDownloadInputsStep, "Add an LLM step that prepares download-compatible records for a later download step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, JsonObject>)ValidatePair, ToolValidatePair, "Validate the current executable plan only through one adjacent pair of steps.", JsonOptions)
    ];

    public IReadOnlyList<AITool> CreatePlanReviewerTools() =>
    [
        AIFunctionFactory.Create((Func<JsonObject>)ReadStructure, ToolReadStructure, "Read the current high-level workflow structure.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)ReadStep, ToolReadStep, "Read one workflow step by id.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, JsonObject>)ReconnectStep, ToolReconnectStep, "Reconnect one step to a new single upstream source step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)AutowireStep, ToolAutowireStep, "Recompute the safest upstream source wiring for one existing step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, JsonObject>)UpdateStepInstruction, ToolUpdateStepInstruction, "Update the strategic instruction of one non-tool step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)DeleteStep, ToolDeleteStep, "Delete one workflow step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, JsonObject>)MoveStepAfter, ToolMoveStepAfter, "Move one workflow step after another step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, string?, JsonObject>)AddDownloadStep, ToolAddDownloadStep, "Add a download step after a compatible source step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, string?, string?, JsonObject>)AddPrepareDownloadInputsStep, ToolAddPrepareDownloadInputsStep, "Add an LLM step that prepares download-compatible records for a later download step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, string?, string?, JsonObject>)AddExtractStep, ToolAddExtractStep, "Add an extraction step that turns source evidence into structured JSON records.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, string?, string?, JsonObject>)AddFilterStep, ToolAddFilterStep, "Add a filtering step that keeps only records matching the criteria.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, string?, string?, JsonObject>)AddRankStep, ToolAddRankStep, "Add a ranking step that orders records by the given criteria.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, string?, string?, string?, string?, JsonObject>)AddAnswerStep, ToolAddAnswerStep, "Add a final answer-writing step.", JsonOptions),
        AIFunctionFactory.Create((Func<string?, JsonObject>)MarkResultStep, ToolMarkResultStep, "Mark one existing workflow step as the explicit result step.", JsonOptions),
        AIFunctionFactory.Create((Func<JsonObject>)ValidateFull, ToolValidateFull, "Validate the entire compiled executable plan.", JsonOptions)
    ];

    [Description("Read the current high-level workflow structure.")]
    public JsonObject ReadStructure() =>
        CreateSuccess(
            ToolReadStructure,
            new JsonObject
            {
                ["goal"] = _goal,
                ["resultStepId"] = string.IsNullOrWhiteSpace(_resultStepId) ? null : JsonValue.Create(_resultStepId),
                ["stepCount"] = _steps.Count,
                ["steps"] = new JsonArray(_steps.Select(BuildStepSummaryNode).ToArray())
            });

    [Description("Read one workflow step by id.")]
    public JsonObject ReadStep(
        [Description("Workflow step id to inspect.")] string? stepId = null)
    {
        var step = GetRequiredStep(stepId);
        return CreateSuccess(
            ToolReadStep,
            new JsonObject
            {
                ["step"] = BuildStepSummaryNode(step),
                ["compiledStep"] = PlanJsonProfiles.SerializeToNode(CompileStep(step), PlanModelProfile.Draft)
            });
    }

    [Description("Read one adjacent pair of workflow steps by pair index.")]
    public JsonObject ReadPair(
        [Description("Zero-based pair index. Pair 0 means steps[0] -> steps[1].")] int? pairIndex = null)
    {
        if (pairIndex is null || pairIndex < 0)
            return CreateFailure(ToolReadPair, "pair_index_invalid", "pairIndex must be a non-negative integer.");

        if (pairIndex >= _steps.Count - 1)
        {
            return CreateSuccess(
                ToolReadPair,
                new JsonObject
                {
                    ["exists"] = false,
                    ["pairIndex"] = pairIndex.Value,
                    ["stepCount"] = _steps.Count
                });
        }

        var fromStep = _steps[pairIndex.Value];
        var toStep = _steps[pairIndex.Value + 1];
        return CreateSuccess(
            ToolReadPair,
            new JsonObject
            {
                ["exists"] = true,
                ["pairIndex"] = pairIndex.Value,
                ["from"] = BuildStepSummaryNode(fromStep),
                ["to"] = BuildStepSummaryNode(toStep),
                ["compatibilityHint"] = BuildPairCompatibilityHint(fromStep, toStep)
            });
    }

    [Description("Set the current workflow goal.")]
    public JsonObject SetGoal(
        [Description("Workflow goal text.")] string? goal = null)
    {
        if (string.IsNullOrWhiteSpace(goal))
            return CreateFailure(ToolSetGoal, "goal_missing", "goal is required.");

        var before = _goal;
        _goal = goal.Trim();
        return CreateSuccess(
            ToolSetGoal,
            new JsonObject
            {
                ["beforeGoal"] = before,
                ["afterGoal"] = _goal
            });
    }

    [Description("Add a search step with query and limit.")]
    public JsonObject AddSearchStep(
        [Description("Insert after this step id. Use null to append at the end.")] string? afterStepId = null,
        [Description("Search query text.")] string? query = null,
        [Description("Maximum number of search results.")] int? limit = null,
        [Description("Optional explicit step id. Omit to auto-generate one.")] string? stepId = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return CreateFailure(ToolAddSearchStep, "query_missing", "query is required.");

        var step = new WorkflowStepSpec
        {
            Id = NormalizeOrGenerateStepId(stepId),
            Kind = WorkflowStepKind.Search,
            Query = query.Trim(),
            Limit = limit is > 0 ? limit.Value : 10
        };

        return InsertStep(ToolAddSearchStep, afterStepId, step);
    }

    [Description("Add a download step after a compatible source step.")]
    public JsonObject AddDownloadStep(
        [Description("Insert after this step id. Use null to append at the end.")] string? afterStepId = null,
        [Description("Optional source step id whose output should feed the download step. Omit to use the nearest compatible previous step.")] string? sourceStepId = null,
        [Description("Optional explicit step id. Omit to auto-generate one.")] string? stepId = null)
    {
        var sourceResolution = ResolveDownloadSource(sourceStepId, afterStepId);
        if (sourceResolution.ErrorCode is not null)
        {
            return CreateFailure(
                ToolAddDownloadStep,
                sourceResolution.ErrorCode,
                sourceResolution.ErrorMessage ?? "Could not resolve a download source.");
        }

        var source = sourceResolution.Sources[0];
        if (!CanFeedDownload(source))
        {
            return CreateFailure(
                ToolAddDownloadStep,
                "source_not_download_compatible",
                $"Step '{source.Id}' does not emit records directly compatible with download. Add a prepare-download-inputs step first.");
        }

        var step = new WorkflowStepSpec
        {
            Id = NormalizeOrGenerateStepId(stepId),
            Kind = WorkflowStepKind.Download,
            SourceStepIds = [source.Id]
        };

        return InsertStep(ToolAddDownloadStep, afterStepId, step);
    }

    [Description("Add an LLM step that prepares download-compatible records for a later download step.")]
    public JsonObject AddPrepareDownloadInputsStep(
        [Description("Insert after this step id. Use null to append at the end.")] string? afterStepId = null,
        [Description("Optional source step id whose output should be transformed into download inputs. Omit to aggregate compatible previous search-like steps.")] string? sourceStepId = null,
        [Description("Short description of how to identify or normalize downloadable records.")] string? preparationGoal = null,
        [Description("Optional explicit step id. Omit to auto-generate one.")] string? stepId = null)
    {
        var sourceResolution = ResolvePrepareDownloadSources(sourceStepId, afterStepId);
        if (sourceResolution.ErrorCode is not null || sourceResolution.Sources.Count == 0)
        {
            return CreateFailure(
                ToolAddPrepareDownloadInputsStep,
                sourceResolution.ErrorCode ?? "source_not_structured",
                sourceResolution.ErrorMessage ?? "No compatible previous step is available for preparing download inputs.");
        }

        var step = new WorkflowStepSpec
        {
            Id = NormalizeOrGenerateStepId(stepId),
            Kind = WorkflowStepKind.PrepareDownloadInputs,
            SourceStepIds = [.. sourceResolution.Sources.Select(source => source.Id)],
            Instruction = NormalizeInstruction(preparationGoal, "Identify downloadable source records and normalize them for the download tool.")
        };

        return InsertStep(ToolAddPrepareDownloadInputsStep, afterStepId, step);
    }

    [Description("Add an extraction step that turns source evidence into structured JSON records.")]
    public JsonObject AddExtractStep(
        [Description("Insert after this step id. Use null to append at the end.")] string? afterStepId = null,
        [Description("Optional source step id to analyze. Omit to use the immediately relevant previous step.")] string? sourceStepId = null,
        [Description("What structured facts should be extracted.")] string? extractionGoal = null,
        [Description("Optional explicit step id. Omit to auto-generate one.")] string? stepId = null)
    {
        var sourceResolution = ResolveReasoningSources(
            sourceStepId,
            afterStepId,
            CanFeedReasoningInput,
            "No compatible previous step is available for extraction.");
        if (sourceResolution.ErrorCode is not null || sourceResolution.Sources.Count == 0)
        {
            return CreateFailure(
                ToolAddExtractStep,
                sourceResolution.ErrorCode ?? "source_not_structured",
                sourceResolution.ErrorMessage ?? "No compatible previous step is available for extraction.");
        }

        var step = new WorkflowStepSpec
        {
            Id = NormalizeOrGenerateStepId(stepId),
            Kind = WorkflowStepKind.Extract,
            SourceStepIds = [.. sourceResolution.Sources.Select(source => source.Id)],
            Instruction = NormalizeInstruction(extractionGoal, "Extract the structured facts needed by the user request.")
        };

        return InsertStep(ToolAddExtractStep, afterStepId, step);
    }

    [Description("Add a filtering step that keeps only records matching the criteria.")]
    public JsonObject AddFilterStep(
        [Description("Insert after this step id. Use null to append at the end.")] string? afterStepId = null,
        [Description("Optional source step id whose records should be filtered. Omit to use the immediately relevant previous step.")] string? sourceStepId = null,
        [Description("Filtering criteria to apply.")] string? filterGoal = null,
        [Description("Optional explicit step id. Omit to auto-generate one.")] string? stepId = null)
    {
        var sourceResolution = ResolveReasoningSources(
            sourceStepId,
            afterStepId,
            CanFeedReasoningInput,
            "No compatible previous step is available for filtering.");
        if (sourceResolution.ErrorCode is not null || sourceResolution.Sources.Count == 0)
        {
            return CreateFailure(
                ToolAddFilterStep,
                sourceResolution.ErrorCode ?? "source_not_reasoning_input",
                sourceResolution.ErrorMessage ?? "No compatible previous step is available for filtering.");
        }

        var step = new WorkflowStepSpec
        {
            Id = NormalizeOrGenerateStepId(stepId),
            Kind = WorkflowStepKind.Filter,
            SourceStepIds = [.. sourceResolution.Sources.Select(source => source.Id)],
            Instruction = NormalizeInstruction(filterGoal, "Keep only the records that satisfy the user constraints.")
        };

        return InsertStep(ToolAddFilterStep, afterStepId, step);
    }

    [Description("Add a ranking step that orders records by the given criteria.")]
    public JsonObject AddRankStep(
        [Description("Insert after this step id. Use null to append at the end.")] string? afterStepId = null,
        [Description("Optional source step id whose records should be ranked. Omit to use the immediately relevant previous step.")] string? sourceStepId = null,
        [Description("Ranking criteria to apply.")] string? rankingGoal = null,
        [Description("Optional explicit step id. Omit to auto-generate one.")] string? stepId = null)
    {
        var sourceResolution = ResolveReasoningSources(
            sourceStepId,
            afterStepId,
            CanFeedReasoningInput,
            "No compatible previous step is available for ranking.");
        if (sourceResolution.ErrorCode is not null || sourceResolution.Sources.Count == 0)
        {
            return CreateFailure(
                ToolAddRankStep,
                sourceResolution.ErrorCode ?? "source_not_reasoning_input",
                sourceResolution.ErrorMessage ?? "No compatible previous step is available for ranking.");
        }

        var step = new WorkflowStepSpec
        {
            Id = NormalizeOrGenerateStepId(stepId),
            Kind = WorkflowStepKind.Rank,
            SourceStepIds = [.. sourceResolution.Sources.Select(source => source.Id)],
            Instruction = NormalizeInstruction(rankingGoal, "Rank the records according to the user-visible recommendation criteria.")
        };

        return InsertStep(ToolAddRankStep, afterStepId, step);
    }

    [Description("Add a final answer-writing step.")]
    public JsonObject AddAnswerStep(
        [Description("Insert after this step id. Use null to append at the end.")] string? afterStepId = null,
        [Description("Optional source step id whose records should feed the final answer. Omit to use the immediately relevant previous step.")] string? sourceStepId = null,
        [Description("What the final answer should emphasize.")] string? answerGoal = null,
        [Description("Output language, for example Russian or English.")] string? outputLanguage = null,
        [Description("Optional explicit step id. Omit to auto-generate one.")] string? stepId = null)
    {
        var sourceResolution = ResolveReasoningSources(
            sourceStepId,
            afterStepId,
            CanFeedReasoningInput,
            "No compatible previous step is available for the final answer.");
        if (sourceResolution.ErrorCode is not null || sourceResolution.Sources.Count == 0)
        {
            return CreateFailure(
                ToolAddAnswerStep,
                sourceResolution.ErrorCode ?? "source_not_reasoning_input",
                sourceResolution.ErrorMessage ?? "No compatible previous step is available for the final answer.");
        }

        var step = new WorkflowStepSpec
        {
            Id = NormalizeOrGenerateStepId(stepId),
            Kind = WorkflowStepKind.Answer,
            SourceStepIds = [.. sourceResolution.Sources.Select(source => source.Id)],
            Instruction = NormalizeInstruction(answerGoal, "Compose the final user-facing answer."),
            OutputLanguage = string.IsNullOrWhiteSpace(outputLanguage) ? "Russian" : outputLanguage.Trim()
        };

        return InsertStep(ToolAddAnswerStep, afterStepId, step);
    }

    [Description("Mark one existing workflow step as the explicit result step.")]
    public JsonObject MarkResultStep(
        [Description("Workflow step id that should be treated as the explicit result of the workflow.")] string? stepId = null)
    {
        var step = GetRequiredStep(stepId);
        var previousResultStepId = _resultStepId;
        _resultStepId = step.Id;

        return CreateSuccess(
            ToolMarkResultStep,
            new JsonObject
            {
                ["resultStepId"] = _resultStepId,
                ["previousResultStepId"] = string.IsNullOrWhiteSpace(previousResultStepId) ? null : JsonValue.Create(previousResultStepId),
                ["step"] = BuildStepSummaryNode(step)
            });
    }

    [Description("Recompute the safest upstream source wiring for one existing step.")]
    public JsonObject AutowireStep(
        [Description("Workflow step id to autowire.")] string? stepId = null)
    {
        var step = GetRequiredStep(stepId);
        if (step.Kind == WorkflowStepKind.Search)
        {
            return CreateFailure(
                ToolAutowireStep,
                "step_has_no_source",
                $"Search step '{step.Id}' does not accept upstream sources.");
        }

        var resolution = ResolveAutoSourcesForExistingStep(step);
        if (resolution.ErrorCode is not null || resolution.Sources.Count == 0)
        {
            return CreateFailure(
                ToolAutowireStep,
                resolution.ErrorCode ?? "autowire_failed",
                resolution.ErrorMessage ?? $"Could not autowire step '{step.Id}'.");
        }

        step.SourceStepIds = [.. resolution.Sources.Select(source => source.Id)];
        return CreateSuccess(
            ToolAutowireStep,
            new JsonObject
            {
                ["stepId"] = step.Id,
                ["newSourceStepIds"] = new JsonArray(step.SourceStepIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray())
            });
    }

    [Description("Reconnect one step to a new single upstream source step.")]
    public JsonObject ReconnectStep(
        [Description("Workflow step id to reconnect.")] string? stepId = null,
        [Description("New single upstream source step id.")] string? sourceStepId = null)
    {
        var step = GetRequiredStep(stepId);
        var source = GetRequiredStep(sourceStepId);
        if (step.Kind == WorkflowStepKind.Search)
        {
            return CreateFailure(
                ToolReconnectStep,
                "step_has_no_source",
                $"Search step '{step.Id}' does not accept upstream sources.");
        }

        if (step.Kind == WorkflowStepKind.Download && !CanFeedDownload(source))
        {
            return CreateFailure(
                ToolReconnectStep,
                "source_not_download_compatible",
                $"Step '{source.Id}' does not emit download-compatible records.");
        }

        if (step.Kind is WorkflowStepKind.Extract or WorkflowStepKind.Filter or WorkflowStepKind.Rank or WorkflowStepKind.Answer or WorkflowStepKind.PrepareDownloadInputs
            && GetOutputKind(source) == WorkflowOutputKind.AnswerText)
        {
            return CreateFailure(
                ToolReconnectStep,
                "source_not_structured",
                $"Step '{source.Id}' outputs final text and cannot feed step '{step.Id}'.");
        }

        step.SourceStepIds = [source.Id];
        return CreateSuccess(
            ToolReconnectStep,
            new JsonObject
            {
                ["stepId"] = step.Id,
                ["newSourceStepIds"] = new JsonArray(JsonValue.Create(source.Id))
            });
    }

    [Description("Update the strategic instruction of one non-tool step.")]
    public JsonObject UpdateStepInstruction(
        [Description("Workflow step id to update.")] string? stepId = null,
        [Description("New strategic instruction text.")] string? instruction = null)
    {
        var step = GetRequiredStep(stepId);
        if (step.Kind is WorkflowStepKind.Search or WorkflowStepKind.Download)
        {
            return CreateFailure(
                ToolUpdateStepInstruction,
                "instruction_not_supported",
                $"Step '{step.Id}' is a tool step and does not use a strategic instruction.");
        }

        step.Instruction = NormalizeInstruction(instruction, step.Instruction ?? string.Empty);
        return CreateSuccess(
            ToolUpdateStepInstruction,
            new JsonObject
            {
                ["stepId"] = step.Id,
                ["instruction"] = step.Instruction
            });
    }

    [Description("Delete one workflow step.")]
    public JsonObject DeleteStep(
        [Description("Workflow step id to delete.")] string? stepId = null)
    {
        var step = GetRequiredStep(stepId);
        _steps.Remove(step);
        if (string.Equals(_resultStepId, step.Id, StringComparison.Ordinal))
            _resultStepId = null;
        foreach (var consumer in _steps.Where(candidate => candidate.SourceStepIds.Contains(step.Id, StringComparer.Ordinal)))
        {
            consumer.SourceStepIds.RemoveAll(sourceId => string.Equals(sourceId, step.Id, StringComparison.Ordinal));
        }

        return CreateSuccess(
            ToolDeleteStep,
            new JsonObject
            {
                ["deletedStepId"] = step.Id,
                ["remainingStepCount"] = _steps.Count
            });
    }

    [Description("Move one workflow step after another step.")]
    public JsonObject MoveStepAfter(
        [Description("Workflow step id to move.")] string? stepId = null,
        [Description("Destination step id after which the step should be placed. Use null to move to the beginning.")] string? afterStepId = null)
    {
        var step = GetRequiredStep(stepId);
        _steps.Remove(step);

        var insertIndex = 0;
        if (!string.IsNullOrWhiteSpace(afterStepId))
        {
            var afterIndex = _steps.FindIndex(candidate => string.Equals(candidate.Id, afterStepId.Trim(), StringComparison.Ordinal));
            if (afterIndex < 0)
            {
                _steps.Insert(Math.Min(insertIndex, _steps.Count), step);
                return CreateFailure(ToolMoveStepAfter, "after_step_not_found", $"Step '{afterStepId}' was not found.");
            }

            insertIndex = afterIndex + 1;
        }

        _steps.Insert(Math.Min(insertIndex, _steps.Count), step);
        return CreateSuccess(
            ToolMoveStepAfter,
            new JsonObject
            {
                ["stepId"] = step.Id,
                ["afterStepId"] = string.IsNullOrWhiteSpace(afterStepId) ? null : JsonValue.Create(afterStepId.Trim()),
                ["newIndex"] = _steps.FindIndex(candidate => string.Equals(candidate.Id, step.Id, StringComparison.Ordinal))
            });
    }

    [Description("Validate the current executable plan only through one adjacent pair of steps.")]
    public JsonObject ValidatePair(
        [Description("Upstream step id of the adjacent pair.")] string? fromStepId = null,
        [Description("Downstream step id of the adjacent pair.")] string? toStepId = null)
    {
        if (string.IsNullOrWhiteSpace(fromStepId) || string.IsNullOrWhiteSpace(toStepId))
            return CreateFailure(ToolValidatePair, "pair_id_missing", "Both fromStepId and toStepId are required.");

        var fromIndex = _steps.FindIndex(step => string.Equals(step.Id, fromStepId.Trim(), StringComparison.Ordinal));
        var toIndex = _steps.FindIndex(step => string.Equals(step.Id, toStepId.Trim(), StringComparison.Ordinal));
        if (fromIndex < 0 || toIndex < 0)
        {
            return CreateFailure(
                ToolValidatePair,
                "pair_step_missing",
                $"Could not find adjacent pair '{fromStepId}' -> '{toStepId}' in the workflow structure.");
        }

        if (toIndex != fromIndex + 1)
        {
            return CreateFailure(
                ToolValidatePair,
                "pair_not_adjacent",
                $"Steps '{fromStepId}' and '{toStepId}' are not adjacent in the workflow structure.");
        }

        var prefixPlan = CompilePlan(_steps.Take(toIndex + 1).ToList());
        JsonObject result;
        if (TryValidateStructure(prefixPlan, out var structuralIssue))
        {
            result = CreateSuccess(
                ToolValidatePair,
                new JsonObject
                {
                    ["ok"] = true,
                    ["shape"] = BuildShapeSignature(prefixPlan)
                });
        }
        else
        {
            result = structuralIssue is null
                ? CreateFailure(ToolValidatePair, "invalid_plan", "Pair validation failed.")
                : CreateValidationFailure(ToolValidatePair, structuralIssue);
        }

        result["pair"] = new JsonObject
        {
            ["fromStepId"] = fromStepId.Trim(),
            ["toStepId"] = toStepId.Trim()
        };
        result["validatedScope"] = "prefix_through_to_step";
        return result;
    }

    [Description("Validate the entire compiled executable plan.")]
    public JsonObject ValidateFull()
    {
        var validation = ValidateExecutablePlan();
        if (!validation.IsValid)
        {
            if (validation.StructuralIssue is not null)
                return CreateValidationFailure(ToolValidateFull, validation.StructuralIssue);

            return CreateSemanticFailure(ToolValidateFull, validation.SemanticIssue!);
        }

        return CreateSuccess(
            ToolValidateFull,
            new JsonObject
            {
                ["ok"] = true,
                ["shape"] = BuildShapeSignature(validation.Plan),
                ["semanticOk"] = true,
                ["resultStepId"] = string.IsNullOrWhiteSpace(_resultStepId) ? null : JsonValue.Create(_resultStepId)
            });
    }

    public ExecutablePlanValidationResult ValidateExecutablePlan()
    {
        var plan = BuildPlan();
        if (!TryValidateStructure(plan, out var structuralIssue))
        {
            return new ExecutablePlanValidationResult(
                plan,
                IsValid: false,
                StructuralIssue: structuralIssue,
                SemanticIssue: null);
        }

        if (!TryValidateSemantics(out var semanticIssue))
        {
            return new ExecutablePlanValidationResult(
                plan,
                IsValid: false,
                StructuralIssue: null,
                SemanticIssue: semanticIssue);
        }

        return new ExecutablePlanValidationResult(
            plan,
            IsValid: true,
            StructuralIssue: null,
            SemanticIssue: null);
    }

    public bool TryValidateSemantics(out SemanticValidationIssue? issue)
    {
        issue = null;
        if (_steps.Count == 0)
        {
            issue = new SemanticValidationIssue(
                "semantic_plan_empty",
                "The workflow does not contain any steps, so it cannot reach a deliverable.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(_resultStepId))
        {
            issue = new SemanticValidationIssue(
                "result_step_missing",
                "The workflow does not explicitly mark any step as the result step.",
                new JsonObject
                {
                    ["stepCount"] = _steps.Count
                });
            return false;
        }

        WorkflowStepSpec resultStep;
        try
        {
            resultStep = GetRequiredStep(_resultStepId);
        }
        catch (InvalidOperationException)
        {
            issue = new SemanticValidationIssue(
                "result_step_missing",
                $"The workflow marks step '{_resultStepId}' as the result, but that step does not exist.",
                new JsonObject
                {
                    ["resultStepId"] = _resultStepId
                });
            return false;
        }

        var intent = BuildIntentProfile();
        if (intent.RequiresUserFacingAnswer && resultStep.Kind != WorkflowStepKind.Answer)
        {
            issue = new SemanticValidationIssue(
                "result_step_not_user_facing",
                $"The explicitly marked result step '{resultStep.Id}' has kind '{resultStep.Kind}', but this intent requires a user-facing answer step.",
                new JsonObject
                {
                    ["resultStepId"] = resultStep.Id,
                    ["resultStepKind"] = resultStep.Kind.ToString()
                });
            return false;
        }

        if (intent.RequiresEvidenceBackedSynthesis && AnswerUsesOnlyRawSearchEvidence(resultStep))
        {
            issue = new SemanticValidationIssue(
                "intent_coverage_too_shallow",
                "The final answer relies only on raw search results. For this intent, the plan must include richer evidence gathering or transformation before the user-facing answer.",
                new JsonObject
                {
                    ["resultStepId"] = resultStep.Id,
                    ["goal"] = _goal,
                    ["userQuery"] = _userQuery
                });
            return false;
        }

        return true;
    }

    private JsonObject InsertStep(string toolName, string? afterStepId, WorkflowStepSpec step)
    {
        if (_steps.Any(existing => string.Equals(existing.Id, step.Id, StringComparison.Ordinal)))
            return CreateFailure(toolName, "step_id_duplicate", $"Step id '{step.Id}' already exists.");

        var insertIndex = _steps.Count;
        if (!string.IsNullOrWhiteSpace(afterStepId))
        {
            var afterIndex = _steps.FindIndex(candidate => string.Equals(candidate.Id, afterStepId.Trim(), StringComparison.Ordinal));
            if (afterIndex < 0)
                return CreateFailure(toolName, "after_step_not_found", $"Step '{afterStepId}' was not found.");

            insertIndex = afterIndex + 1;
        }

        _steps.Insert(insertIndex, step);
        return CreateSuccess(
            toolName,
            new JsonObject
            {
                ["stepId"] = step.Id,
                ["insertIndex"] = insertIndex,
                ["step"] = BuildStepSummaryNode(step)
            });
    }

    private WorkflowStepSpec GetRequiredStep(string? stepId)
    {
        if (string.IsNullOrWhiteSpace(stepId))
            throw new InvalidOperationException("stepId is required.");

        var normalized = stepId.Trim();
        return _steps.FirstOrDefault(step => string.Equals(step.Id, normalized, StringComparison.Ordinal))
               ?? throw new InvalidOperationException($"Step '{normalized}' was not found.");
    }

    private string NormalizeOrGenerateStepId(string? stepId)
    {
        if (!string.IsNullOrWhiteSpace(stepId))
            return stepId.Trim();

        var index = 1;
        while (_steps.Any(step => string.Equals(step.Id, $"s{index}", StringComparison.Ordinal)))
            index++;

        return $"s{index}";
    }

    private SourceResolution ResolveDownloadSource(string? sourceStepId, string? afterStepId)
    {
        if (!string.IsNullOrWhiteSpace(sourceStepId))
        {
            var explicitSource = GetRequiredStep(sourceStepId);
            return SourceResolution.FromSources([explicitSource]);
        }

        var candidates = FindTerminalCompatiblePreviousSteps(afterStepId, CanFeedDownload);
        return candidates.Count switch
        {
            0 => SourceResolution.FromError(
                "source_not_found",
                "No compatible previous step is available for download."),
            1 => SourceResolution.FromSources(candidates),
            _ => SourceResolution.FromError(
                "download_source_ambiguous",
                $"Found {candidates.Count} compatible prior branches for a download step. Insert a prepare-download-inputs step first, or choose an explicit single source.")
        };
    }

    private SourceResolution ResolvePrepareDownloadSources(string? sourceStepId, string? afterStepId)
    {
        if (!string.IsNullOrWhiteSpace(sourceStepId))
        {
            var explicitSource = GetRequiredStep(sourceStepId);
            return SourceResolution.FromSources([explicitSource]);
        }

        var candidates = FindTerminalCompatiblePreviousSteps(afterStepId, CanFeedPrepareDownloadInput);
        return candidates.Count == 0
            ? SourceResolution.FromError(
                "source_not_structured",
                "No compatible previous step is available for preparing download inputs.")
            : SourceResolution.FromSources(candidates);
    }

    private SourceResolution ResolveReasoningSources(
        string? sourceStepId,
        string? afterStepId,
        Func<WorkflowStepSpec, bool> predicate,
        string noSourceMessage)
    {
        if (!string.IsNullOrWhiteSpace(sourceStepId))
        {
            var explicitSource = GetRequiredStep(sourceStepId);
            return SourceResolution.FromSources([explicitSource]);
        }

        var candidates = FindTerminalCompatiblePreviousSteps(afterStepId, predicate);
        return candidates.Count == 0
            ? SourceResolution.FromError("source_not_found", noSourceMessage)
            : SourceResolution.FromSources(candidates);
    }

    private SourceResolution ResolveAutoSourcesForExistingStep(WorkflowStepSpec step)
    {
        var stepIndex = _steps.FindIndex(candidate => string.Equals(candidate.Id, step.Id, StringComparison.Ordinal));
        if (stepIndex < 0)
            return SourceResolution.FromError("step_not_found", $"Step '{step.Id}' was not found.");

        var previousSteps = _steps.Take(stepIndex).ToList();
        return step.Kind switch
        {
            WorkflowStepKind.Download => ResolveDownloadSourceForExistingStep(previousSteps),
            WorkflowStepKind.PrepareDownloadInputs => ResolveAutoSources(previousSteps, CanFeedPrepareDownloadInput, "No compatible previous step is available for preparing download inputs."),
            WorkflowStepKind.Extract => ResolveAutoSources(previousSteps, CanFeedReasoningInput, "No compatible previous step is available for extraction."),
            WorkflowStepKind.Filter => ResolveAutoSources(previousSteps, CanFeedReasoningInput, "No compatible previous step is available for filtering."),
            WorkflowStepKind.Rank => ResolveAutoSources(previousSteps, CanFeedReasoningInput, "No compatible previous step is available for ranking."),
            WorkflowStepKind.Answer => ResolveAutoSources(previousSteps, CanFeedReasoningInput, "No compatible previous step is available for the final answer."),
            _ => SourceResolution.FromError("step_has_no_source", $"Step '{step.Id}' does not support autowiring.")
        };
    }

    private SourceResolution ResolveDownloadSourceForExistingStep(IReadOnlyList<WorkflowStepSpec> previousSteps)
    {
        var candidates = FindTerminalCompatibleSteps(previousSteps, CanFeedDownload);
        return candidates.Count switch
        {
            0 => SourceResolution.FromError("source_not_found", "No compatible previous step is available for download."),
            1 => SourceResolution.FromSources(candidates),
            _ => SourceResolution.FromError(
                "download_source_ambiguous",
                $"Found {candidates.Count} compatible prior branches for a download step. Insert a prepare-download-inputs step first, or reconnect to one explicit source.")
        };
    }

    private SourceResolution ResolveAutoSources(
        IReadOnlyList<WorkflowStepSpec> previousSteps,
        Func<WorkflowStepSpec, bool> predicate,
        string noSourceMessage)
    {
        var candidates = FindTerminalCompatibleSteps(previousSteps, predicate);
        return candidates.Count == 0
            ? SourceResolution.FromError("source_not_found", noSourceMessage)
            : SourceResolution.FromSources(candidates);
    }

    private List<WorkflowStepSpec> FindTerminalCompatiblePreviousSteps(
        string? afterStepId,
        Func<WorkflowStepSpec, bool> predicate)
    {
        var insertionIndex = ResolveInsertionIndex(afterStepId);
        var previousSteps = _steps.Take(insertionIndex).ToList();
        return FindTerminalCompatibleSteps(previousSteps, predicate);
    }

    private List<WorkflowStepSpec> FindTerminalCompatibleSteps(
        IReadOnlyList<WorkflowStepSpec> previousSteps,
        Func<WorkflowStepSpec, bool> predicate)
    {
        var candidates = previousSteps
            .Where(predicate)
            .ToList();
        if (candidates.Count <= 1)
            return candidates;

        var consumedIds = previousSteps
            .SelectMany(step => step.SourceStepIds)
            .ToHashSet(StringComparer.Ordinal);

        var terminalCandidates = candidates
            .Where(step => !consumedIds.Contains(step.Id))
            .ToList();

        return terminalCandidates.Count > 0 ? terminalCandidates : candidates;
    }

    private int ResolveInsertionIndex(string? afterStepId)
    {
        if (string.IsNullOrWhiteSpace(afterStepId))
            return _steps.Count;

        var afterIndex = _steps.FindIndex(candidate => string.Equals(candidate.Id, afterStepId.Trim(), StringComparison.Ordinal));
        if (afterIndex < 0)
            throw new InvalidOperationException($"Step '{afterStepId}' was not found.");

        return afterIndex + 1;
    }

    private List<WorkflowStepSpec> GetTerminalSteps()
    {
        var consumedIds = _steps
            .SelectMany(step => step.SourceStepIds)
            .ToHashSet(StringComparer.Ordinal);

        return _steps
            .Where(step => !consumedIds.Contains(step.Id))
            .ToList();
    }

    private IntentProfile BuildIntentProfile()
    {
        var combinedText = string.Join(
            "\n",
            new[]
            {
                _userQuery,
                _goal
            }.Where(static value => !string.IsNullOrWhiteSpace(value)));

        return new IntentProfile
        {
            RequiresUserFacingAnswer = ContainsAny(
                combinedText,
                "answer",
                "recommend",
                "suggest",
                "advise",
                "explain",
                "summarize",
                "list",
                "tell me",
                "посовет",
                "рекоменд",
                "объясн",
                "сумм",
                "перечисл",
                "ответ"),
            RequiresEvidenceBackedSynthesis = ContainsAny(
                combinedText,
                "recommend",
                "suggest",
                "best",
                "choose",
                "compare",
                "rank",
                "price",
                "budget",
                "cost",
                "under",
                "eur",
                "usd",
                "посовет",
                "рекоменд",
                "лучш",
                "выбер",
                "сравн",
                "цен",
                "бюджет",
                "до ")
        };
    }

    private bool AnswerUsesOnlyRawSearchEvidence(WorkflowStepSpec answerStep)
    {
        if (answerStep.SourceStepIds.Count == 0)
            return true;

        return answerStep.SourceStepIds
            .Select(GetRequiredStep)
            .All(source => GetOutputKind(source) == WorkflowOutputKind.SearchResults);
    }

    private PlanDefinition CompilePlan(IReadOnlyList<WorkflowStepSpec> steps)
    {
        var compiledSteps = new List<PlanStep>(steps.Count);
        foreach (var step in steps)
            compiledSteps.Add(CompileStep(step));

        return new PlanDefinition
        {
            Goal = _goal,
            Steps = compiledSteps
        };
    }

    private PlanStep CompileStep(WorkflowStepSpec step)
    {
        return step.Kind switch
        {
            WorkflowStepKind.Search => CompileSearchStep(step),
            WorkflowStepKind.Download => CompileDownloadStep(step),
            WorkflowStepKind.PrepareDownloadInputs => CompilePrepareDownloadInputsStep(step),
            WorkflowStepKind.Extract => CompileExtractStep(step),
            WorkflowStepKind.Filter => CompileFilterStep(step),
            WorkflowStepKind.Rank => CompileRankStep(step),
            WorkflowStepKind.Answer => CompileAnswerStep(step),
            _ => throw new InvalidOperationException($"Unsupported workflow step kind '{step.Kind}'.")
        };
    }

    private PlanStep CompileSearchStep(WorkflowStepSpec step) =>
        new()
        {
            Id = step.Id,
            Kind = PlanStepKinds.Tool,
            CapabilityId = SearchCapabilityId,
            In = new Dictionary<string, JsonNode?>
            {
                ["query"] = JsonValue.Create(step.Query ?? string.Empty),
                ["limit"] = JsonValue.Create(step.Limit ?? 10)
            }
        };

    private PlanStep CompileDownloadStep(WorkflowStepSpec step)
    {
        var sourceStepId = GetSingleSourceStepId(step);
        var source = GetRequiredStep(sourceStepId);
        if (!CanFeedDownload(source))
        {
            throw new InvalidOperationException(
                $"Step '{step.Id}' cannot compile because source '{source.Id}' is not download-compatible.");
        }

        return new PlanStep
        {
            Id = step.Id,
            Kind = PlanStepKinds.Tool,
            CapabilityId = DownloadCapabilityId,
            In = new Dictionary<string, JsonNode?>
            {
                ["page"] = CreateBinding($"{source.Id}.results", PlanInputBindingMode.Map)
            }
        };
    }

    private PlanStep CompilePrepareDownloadInputsStep(WorkflowStepSpec step)
    {
        var inputs = BuildReasoningInputs(step);

        return new PlanStep
        {
            Id = step.Id,
            Kind = PlanStepKinds.Llm,
            In = inputs,
            SystemPrompt = BuildPrepareDownloadInputsPrompt(step.Instruction ?? string.Empty),
            UserPrompt = "Use only the supplied records and documents. Return only JSON records directly usable by the next download step.",
            Out = new PlanStepOutputContract
            {
                Format = PlanStepOutputFormats.Json
            }
        };
    }

    private PlanStep CompileExtractStep(WorkflowStepSpec step)
    {
        var inputs = BuildReasoningInputs(step);

        return new PlanStep
        {
            Id = step.Id,
            Kind = PlanStepKinds.Llm,
            In = inputs,
            SystemPrompt = BuildExtractPrompt(step.Instruction ?? string.Empty),
            UserPrompt = "Analyze only the supplied records and documents and return only the requested structured JSON records.",
            Out = new PlanStepOutputContract
            {
                Format = PlanStepOutputFormats.Json
            }
        };
    }

    private PlanStep CompileFilterStep(WorkflowStepSpec step)
    {
        return new PlanStep
        {
            Id = step.Id,
            Kind = PlanStepKinds.Llm,
            In = BuildReasoningInputs(step),
            SystemPrompt = BuildFilterPrompt(step.Instruction ?? string.Empty),
            UserPrompt = "Filter the supplied records and documents according to the stated criteria and preserve the surviving useful fields.",
            Out = new PlanStepOutputContract
            {
                Format = PlanStepOutputFormats.Json
            }
        };
    }

    private PlanStep CompileRankStep(WorkflowStepSpec step)
    {
        return new PlanStep
        {
            Id = step.Id,
            Kind = PlanStepKinds.Llm,
            In = BuildReasoningInputs(step),
            SystemPrompt = BuildRankPrompt(step.Instruction ?? string.Empty),
            UserPrompt = "Rank the supplied records and documents according to the stated criteria and return an ordered JSON array.",
            Out = new PlanStepOutputContract
            {
                Format = PlanStepOutputFormats.Json
            }
        };
    }

    private PlanStep CompileAnswerStep(WorkflowStepSpec step)
    {
        return new PlanStep
        {
            Id = step.Id,
            Kind = PlanStepKinds.Llm,
            In = BuildReasoningInputs(step),
            SystemPrompt = BuildAnswerPrompt(step.Instruction ?? string.Empty, step.OutputLanguage ?? "Russian"),
            UserPrompt = "Write the final answer using only the supplied records and documents.",
            Out = new PlanStepOutputContract
            {
                Format = PlanStepOutputFormats.String
            }
        };
    }

    private bool TryValidateStructure(PlanDefinition plan, out PlanValidationIssue? issue)
    {
        try
        {
            PlanSanitizer.Sanitize(plan, PlanModelProfile.Draft);
            PlanNormalizer.Normalize(plan, _workflowTools);
            if (PlanValidator.TryValidate(plan, _workflowTools, callableAgents: null, PlanModelProfile.Draft, out issue))
                return true;

            PlanValidationIssue? graphIssue = null;
            if (issue is not null
                && (string.Equals(issue.Code, "step_output_unused", StringComparison.Ordinal)
                    || string.Equals(issue.Code, "plan_terminal_step_invalid", StringComparison.Ordinal))
                && TryValidateGraphShapeWithExplicitResult(plan, out graphIssue))
            {
                issue = null;
                return true;
            }

            if (graphIssue is not null)
                issue = graphIssue;

            return false;
        }
        catch (Exception ex)
        {
            issue = new PlanValidationIssue("invalid_plan", ex.Message);
            return false;
        }
    }

    private bool TryValidateGraphShapeWithExplicitResult(PlanDefinition plan, out PlanValidationIssue? issue)
    {
        issue = null;
        if (string.IsNullOrWhiteSpace(_resultStepId))
            return false;

        var resultStepId = _resultStepId.Trim();
        var dependentsLookup = PlanDependencyGraph.BuildDependentsLookup(plan.Steps);
        foreach (var step in plan.Steps)
        {
            var hasChildren = dependentsLookup.TryGetValue(step.Id, out var children) && children.Count > 0;
            if (hasChildren || string.Equals(step.Id, resultStepId, StringComparison.Ordinal))
                continue;

            issue = new PlanValidationIssue(
                "step_output_unused",
                $"Step '{step.Id}' does not feed any downstream step. Only the explicitly marked result step may end without downstream consumers.",
                StepId: step.Id);
            return false;
        }

        return true;
    }

    private JsonObject BuildPairCompatibilityHint(WorkflowStepSpec fromStep, WorkflowStepSpec toStep)
    {
        var suggestedSourceIds = GetSuggestedSourceStepIds(toStep);
        var compatible = toStep.SourceStepIds.Contains(fromStep.Id, StringComparer.Ordinal) && IsDomainCompatible(fromStep, toStep);
        return new JsonObject
        {
            ["compatible"] = compatible,
            ["fromOutputKind"] = GetOutputKind(fromStep).ToString(),
            ["toConsumesFrom"] = new JsonArray(toStep.SourceStepIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["suggestedSourceStepIds"] = new JsonArray(suggestedSourceIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["reason"] = compatible
                ? "The downstream step already consumes the upstream step in a domain-compatible way."
                : DescribeCompatibilityFix(fromStep, toStep)
        };
    }

    private bool IsDomainCompatible(WorkflowStepSpec fromStep, WorkflowStepSpec toStep)
    {
        var outputKind = GetOutputKind(fromStep);
        return toStep.Kind switch
        {
            WorkflowStepKind.Download => outputKind is WorkflowOutputKind.SearchResults or WorkflowOutputKind.DownloadRequests,
            WorkflowStepKind.PrepareDownloadInputs => outputKind != WorkflowOutputKind.AnswerText,
            WorkflowStepKind.Extract => outputKind != WorkflowOutputKind.AnswerText,
            WorkflowStepKind.Filter => outputKind != WorkflowOutputKind.AnswerText,
            WorkflowStepKind.Rank => outputKind != WorkflowOutputKind.AnswerText,
            WorkflowStepKind.Answer => outputKind != WorkflowOutputKind.AnswerText,
            _ => true
        };
    }

    private static string DescribeCompatibilityFix(WorkflowStepSpec fromStep, WorkflowStepSpec toStep)
    {
        return toStep.Kind switch
        {
            WorkflowStepKind.Download => $"Reconnect or rewrite '{toStep.Id}' so it consumes download-compatible records from '{fromStep.Id}', or insert a prepare-download-inputs step first.",
            WorkflowStepKind.PrepareDownloadInputs => $"Reconnect '{toStep.Id}' to structured records from '{fromStep.Id}'.",
            WorkflowStepKind.Extract => $"Reconnect '{toStep.Id}' to the evidence source that should be extracted.",
            WorkflowStepKind.Filter => $"Reconnect '{toStep.Id}' to the JSON records it should filter.",
            WorkflowStepKind.Rank => $"Reconnect '{toStep.Id}' to the JSON records it should rank.",
            WorkflowStepKind.Answer => $"Reconnect '{toStep.Id}' to the records that should feed the final answer.",
            _ => $"Review whether '{toStep.Id}' should consume '{fromStep.Id}'."
        };
    }

    private static string BuildShapeSignature(PlanDefinition plan) =>
        string.Join(
            " -> ",
            plan.Steps.Select(step =>
                string.IsNullOrWhiteSpace(step.CapabilityId)
                    ? step.Kind
                    : $"{step.Kind}:{step.CapabilityId}"));

    private WorkflowOutputKind GetOutputKind(WorkflowStepSpec step) =>
        step.Kind switch
        {
            WorkflowStepKind.Search => WorkflowOutputKind.SearchResults,
            WorkflowStepKind.Download => WorkflowOutputKind.DownloadedDocuments,
            WorkflowStepKind.PrepareDownloadInputs => WorkflowOutputKind.DownloadRequests,
            WorkflowStepKind.Extract => WorkflowOutputKind.JsonRecords,
            WorkflowStepKind.Filter => WorkflowOutputKind.JsonRecords,
            WorkflowStepKind.Rank => WorkflowOutputKind.JsonRecords,
            WorkflowStepKind.Answer => WorkflowOutputKind.AnswerText,
            _ => WorkflowOutputKind.JsonRecords
        };

    private static bool CanFeedReasoningInput(WorkflowStepSpec source) =>
        source.Kind != WorkflowStepKind.Answer;

    private static bool CanFeedPrepareDownloadInput(WorkflowStepSpec source) =>
        source.Kind != WorkflowStepKind.Answer;

    private bool CanFeedDownload(WorkflowStepSpec source)
    {
        var outputKind = GetOutputKind(source);
        return outputKind is WorkflowOutputKind.SearchResults or WorkflowOutputKind.DownloadRequests;
    }

    private static string GetSingleSourceStepId(WorkflowStepSpec step)
    {
        if (step.SourceStepIds.Count != 1 || string.IsNullOrWhiteSpace(step.SourceStepIds[0]))
        {
            throw new InvalidOperationException(
                $"Workflow step '{step.Id}' requires exactly one upstream source step.");
        }

        return step.SourceStepIds[0];
    }

    private Dictionary<string, JsonNode?> BuildReasoningInputs(WorkflowStepSpec step)
    {
        if (step.SourceStepIds.Count == 0)
            throw new InvalidOperationException($"Workflow step '{step.Id}' requires at least one upstream source step.");

        var sources = step.SourceStepIds
            .Select(GetRequiredStep)
            .ToList();
        var inputs = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

        var recordSources = sources
            .Where(source => GetOutputKind(source) is not WorkflowOutputKind.DownloadedDocuments and not WorkflowOutputKind.AnswerText)
            .ToList();
        if (recordSources.Count > 0)
            inputs["records"] = CreateArrayBinding(recordSources);

        var documentSources = sources
            .Where(source => GetOutputKind(source) == WorkflowOutputKind.DownloadedDocuments)
            .ToList();
        if (documentSources.Count > 0)
            inputs["documents"] = CreateArrayBinding(documentSources);

        if (inputs.Count == 0)
            throw new InvalidOperationException($"Workflow step '{step.Id}' does not have any reasoning-compatible upstream sources.");

        return inputs;
    }

    private static JsonObject CreateBinding(string referenceWithoutDollar, PlanInputBindingMode mode) =>
        new()
        {
            ["from"] = $"${referenceWithoutDollar}",
            ["mode"] = mode == PlanInputBindingMode.Map ? "map" : "value"
        };

    private static JsonNode CreateArrayBinding(IReadOnlyList<WorkflowStepSpec> sources)
    {
        if (sources.Count == 0)
            throw new InvalidOperationException("At least one source is required.");

        if (sources.Count == 1)
            return CreateBinding($"{sources[0].Id}.results", PlanInputBindingMode.Value);

        return new JsonObject
        {
            ["concat"] = new JsonArray(sources.Select(static source => (JsonNode?)CreateBinding($"{source.Id}.results", PlanInputBindingMode.Value)).ToArray()),
            ["type"] = "array<object>"
        };
    }

    private static string NormalizeInstruction(string? instruction, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(instruction))
            return instruction.Trim();

        return fallback;
    }

    private List<string> GetSuggestedSourceStepIds(WorkflowStepSpec step)
    {
        var resolution = ResolveAutoSourcesForExistingStep(step);
        return resolution.ErrorCode is null
            ? [.. resolution.Sources.Select(source => source.Id)]
            : [];
    }

    private JsonNode BuildStepSummaryNode(WorkflowStepSpec step)
    {
        var node = new JsonObject
        {
            ["id"] = step.Id,
            ["kind"] = step.Kind.ToString(),
            ["outputKind"] = GetOutputKind(step).ToString(),
            ["isResult"] = string.Equals(_resultStepId, step.Id, StringComparison.Ordinal)
        };

        if (step.SourceStepIds.Count > 0)
            node["sourceStepIds"] = new JsonArray(step.SourceStepIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        if (!string.IsNullOrWhiteSpace(step.Query))
            node["query"] = step.Query;
        if (step.Limit is not null)
            node["limit"] = step.Limit.Value;
        if (!string.IsNullOrWhiteSpace(step.Instruction))
            node["instruction"] = step.Instruction;
        if (!string.IsNullOrWhiteSpace(step.OutputLanguage))
            node["outputLanguage"] = step.OutputLanguage;
        if (step.Kind != WorkflowStepKind.Search)
        {
            var suggestedSourceIds = GetSuggestedSourceStepIds(step);
            if (suggestedSourceIds.Count > 0)
            {
                node["suggestedSourceStepIds"] = new JsonArray(suggestedSourceIds.Select(static id => (JsonNode?)JsonValue.Create(id)).ToArray());
            }
        }

        try
        {
            var compiled = CompileStep(step);
            node["compiledKind"] = compiled.Kind;
            node["compiledCapabilityId"] = compiled.CapabilityId;
        }
        catch (Exception ex)
        {
            node["compileError"] = ex.Message;
        }

        return node;
    }

    private static string BuildPrepareDownloadInputsPrompt(string instruction) =>
        $"You are preparing inputs for the download tool. {instruction} Return only JSON records that remain directly compatible with the downstream download capability. Preserve compatible page/url records instead of summarizing them.";

    private static string BuildExtractPrompt(string instruction) =>
        $"You are extracting structured evidence from the supplied input. {instruction} Return only JSON records. Preserve enough fields for downstream filtering, ranking, and answer writing.";

    private static string BuildFilterPrompt(string instruction) =>
        $"You are filtering structured records. {instruction} Keep only the matching records and preserve their useful fields. Return only a JSON array.";

    private static string BuildRankPrompt(string instruction) =>
        $"You are ranking structured records. {instruction} Return only a JSON array ordered from best to worst while preserving record content.";

    private static string BuildAnswerPrompt(string instruction, string outputLanguage) =>
        $"You are writing the final answer in {outputLanguage}. {instruction} Use only the supplied records. Do not invent facts.";

    private static JsonObject CreateSuccess(string toolName, JsonNode? output) =>
        new()
        {
            ["tool"] = toolName,
            ["ok"] = true,
            ["output"] = output?.DeepClone()
        };

    private static JsonObject CreateFailure(string toolName, string code, string message) =>
        new()
        {
            ["tool"] = toolName,
            ["ok"] = false,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };

    private static JsonObject CreateValidationFailure(string toolName, PlanValidationIssue issue)
    {
        var error = new JsonObject
        {
            ["code"] = issue.Code,
            ["message"] = issue.Message,
            ["details"] = JsonSerializer.SerializeToNode(issue, JsonOptions)
        };

        return new JsonObject
        {
            ["tool"] = toolName,
            ["ok"] = false,
            ["error"] = error
        };
    }

    private static JsonObject CreateSemanticFailure(string toolName, SemanticValidationIssue issue)
    {
        var error = new JsonObject
        {
            ["code"] = issue.Code,
            ["message"] = issue.Message
        };
        if (issue.Details is not null)
            error["details"] = issue.Details.DeepClone();

        return new JsonObject
        {
            ["tool"] = toolName,
            ["ok"] = false,
            ["error"] = error
        };
    }

    private static bool ContainsAny(string source, params string[] needles) =>
        needles.Any(needle => source.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private sealed class SourceResolution
    {
        public List<WorkflowStepSpec> Sources { get; init; } = [];

        public string? ErrorCode { get; init; }

        public string? ErrorMessage { get; init; }

        public static SourceResolution FromSources(IEnumerable<WorkflowStepSpec> sources) =>
            new()
            {
                Sources = [.. sources]
            };

        public static SourceResolution FromError(string code, string message) =>
            new()
            {
                ErrorCode = code,
                ErrorMessage = message
            };
    }

    public sealed record SemanticValidationIssue(
        string Code,
        string Message,
        JsonObject? Details = null);

    public sealed record ExecutablePlanValidationResult(
        PlanDefinition Plan,
        bool IsValid,
        PlanValidationIssue? StructuralIssue,
        SemanticValidationIssue? SemanticIssue);

    private sealed class IntentProfile
    {
        public bool RequiresUserFacingAnswer { get; init; }

        public bool RequiresEvidenceBackedSynthesis { get; init; }
    }

    private sealed class WorkflowStepSpec
    {
        public string Id { get; init; } = string.Empty;

        public WorkflowStepKind Kind { get; init; }

        public List<string> SourceStepIds { get; set; } = [];

        public string? Query { get; init; }

        public int? Limit { get; init; }

        public string? Instruction { get; set; }

        public string? OutputLanguage { get; init; }
    }

    private enum WorkflowStepKind
    {
        Search,
        Download,
        PrepareDownloadInputs,
        Extract,
        Filter,
        Rank,
        Answer
    }

    private enum WorkflowOutputKind
    {
        SearchResults,
        DownloadRequests,
        DownloadedDocuments,
        JsonRecords,
        AnswerText
    }
}
