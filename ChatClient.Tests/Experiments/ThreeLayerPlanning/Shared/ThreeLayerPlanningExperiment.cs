using ChatClient.Api.Services;
using System.Text.Json.Nodes;
using ChatClient.Api.PlanningRuntime.LowLevel;
using ChatClient.Api.PlanningRuntime.Outline;
using ChatClient.Api.PlanningRuntime.Runtime;
using ChatClient.Api.PlanningRuntime.Shared;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Scenarios;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;

public sealed class ThreeLayerPlanningExperiment
{
    private readonly IReadOnlyCollection<AppToolDescriptor> _tools;
    private readonly IReadOnlyList<CompactCapabilitySummary> _capabilities;
    private readonly OutlinePlanner _outlinePlanner;
    private readonly OutlineRepairer _outlineRepairer = new();
    private readonly LowLevelPlanner _lowLevelPlanner;
    private readonly LowLevelRepairer _lowLevelRepairer = new();
    private readonly RuntimePlanExecutor _runtimeExecutor;
    private readonly RuntimePlannerCompiler _runtimeCompiler;

    public ThreeLayerPlanningExperiment(
        IExperimentLlmClient llmClient,
        IReadOnlyCollection<AppToolDescriptor> tools)
    {
        ArgumentNullException.ThrowIfNull(llmClient);
        ArgumentNullException.ThrowIfNull(tools);

        _tools = tools;
        _capabilities = CapabilitySummaryBuilder.Build(tools);
        _outlinePlanner = new OutlinePlanner(llmClient);
        _lowLevelPlanner = new LowLevelPlanner(llmClient);
        _runtimeCompiler = new RuntimePlannerCompiler(tools);
        _runtimeExecutor = new RuntimePlanExecutor(llmClient, tools);
    }

    public async Task<ThreeLayerExperimentRun> RunAsync(
        ThreeLayerExperimentScenario scenario,
        int runIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var issues = new List<string>();
        PlanningStageResult<OutlinePlan> outlineStage;
        try
        {
            outlineStage = await _outlinePlanner.CreatePlanAsync(
                new OutlinePlanningRequest
                {
                    UserQuery = scenario.UserQuery,
                    ResultExpectations = scenario.ResultExpectations,
                    Capabilities = _capabilities
                },
                cancellationToken);
        }
        catch (PlanningContractException ex) when (string.Equals(ex.Stage, "outline", StringComparison.OrdinalIgnoreCase))
        {
            issues.AddRange(ex.ContractIssues.Select(issue => $"outline_contract:{issue}"));
            return new ThreeLayerExperimentRun
            {
                RunIndex = runIndex,
                Status = "outline_contract_failed",
                OutlineValid = false,
                LowLevelValid = false,
                RuntimeCompiled = false,
                RuntimeExecuted = false,
                OutlineShape = string.Empty,
                OutlinePlanJson = ex.MaterializedJson ?? string.Empty,
                OutlineRawResponse = ex.RawResponse,
                LowLevelPlanJson = string.Empty,
                RuntimePlanJson = null,
                Issues = issues
            };
        }

        var outlinePlan = outlineStage.Plan;

        var outlineValidation = OutlineValidator.Validate(outlinePlan);
        if (!outlineValidation.IsValid)
        {
            outlinePlan = _outlineRepairer.Repair(outlinePlan, outlineValidation.Issues);
            outlineValidation = OutlineValidator.Validate(outlinePlan);
        }

        AddIssues(issues, outlineValidation.Issues);
        if (!outlineValidation.IsValid)
        {
            return new ThreeLayerExperimentRun
            {
                RunIndex = runIndex,
                Status = "invalid_outline",
                OutlineValid = false,
                LowLevelValid = false,
                RuntimeCompiled = false,
                RuntimeExecuted = false,
                OutlineShape = ShapeSignatureBuilder.BuildOutlineShape(outlinePlan),
                OutlinePlanJson = ExperimentJson.SerializeIndented(outlinePlan),
                OutlineRawResponse = outlineStage.RawResponse,
                LowLevelPlanJson = string.Empty,
                RuntimePlanJson = null,
                Issues = issues
            };
        }

        PlanningStageResult<LowLevelPlan> lowLevelStage;
        try
        {
            lowLevelStage = await _lowLevelPlanner.CreatePlanAsync(
                new LowLevelPlanningRequest
                {
                    OutlinePlan = outlinePlan,
                    Capabilities = _capabilities
                },
                cancellationToken);
        }
        catch (PlanningContractException ex) when (string.Equals(ex.Stage, "low_level", StringComparison.OrdinalIgnoreCase))
        {
            issues.AddRange(ex.ContractIssues.Select(issue => $"low_level_contract:{issue}"));
            return new ThreeLayerExperimentRun
            {
                RunIndex = runIndex,
                Status = "low_level_contract_failed",
                OutlineValid = true,
                LowLevelValid = false,
                RuntimeCompiled = false,
                RuntimeExecuted = false,
                OutlineShape = ShapeSignatureBuilder.BuildOutlineShape(outlinePlan),
                OutlinePlanJson = ExperimentJson.SerializeIndented(outlinePlan),
                OutlineRawResponse = outlineStage.RawResponse,
                LowLevelPlanJson = ex.MaterializedJson ?? string.Empty,
                LowLevelRawResponse = ex.RawResponse,
                RuntimePlanJson = null,
                Issues = issues
            };
        }

        var lowLevelPlan = lowLevelStage.Plan;

        var lowLevelValidation = LowLevelValidator.Validate(lowLevelPlan, outlinePlan, _tools);
        if (!lowLevelValidation.IsValid)
        {
            lowLevelPlan = _lowLevelRepairer.Repair(lowLevelPlan, outlinePlan, _tools, lowLevelValidation.Issues);
            lowLevelValidation = LowLevelValidator.Validate(lowLevelPlan, outlinePlan, _tools);
        }

        AddIssues(issues, lowLevelValidation.Issues);
        if (!lowLevelValidation.IsValid)
        {
            return new ThreeLayerExperimentRun
            {
                RunIndex = runIndex,
                Status = "invalid_lowlevel",
                OutlineValid = true,
                LowLevelValid = false,
                RuntimeCompiled = false,
                RuntimeExecuted = false,
                OutlineShape = ShapeSignatureBuilder.BuildOutlineShape(outlinePlan),
                LowLevelShape = ShapeSignatureBuilder.BuildLowLevelShape(lowLevelPlan),
                OutlinePlanJson = ExperimentJson.SerializeIndented(outlinePlan),
                OutlineRawResponse = outlineStage.RawResponse,
                LowLevelPlanJson = ExperimentJson.SerializeIndented(lowLevelPlan),
                LowLevelRawResponse = lowLevelStage.RawResponse,
                RuntimePlanJson = null,
                Issues = issues
            };
        }

        var compileResult = _runtimeCompiler.Compile(lowLevelPlan);
        AddIssues(issues, compileResult.Issues);
        if (!compileResult.IsSuccess || compileResult.Plan is null)
        {
            return new ThreeLayerExperimentRun
            {
                RunIndex = runIndex,
                Status = "compile_failed",
                OutlineValid = true,
                LowLevelValid = true,
                RuntimeCompiled = false,
                RuntimeExecuted = false,
                OutlineShape = ShapeSignatureBuilder.BuildOutlineShape(outlinePlan),
                LowLevelShape = ShapeSignatureBuilder.BuildLowLevelShape(lowLevelPlan),
                OutlinePlanJson = ExperimentJson.SerializeIndented(outlinePlan),
                OutlineRawResponse = outlineStage.RawResponse,
                LowLevelPlanJson = ExperimentJson.SerializeIndented(lowLevelPlan),
                LowLevelRawResponse = lowLevelStage.RawResponse,
                RuntimePlanJson = null,
                ResultStepId = lowLevelPlan.ResultStepId,
                Issues = issues
            };
        }

        var runtimePlan = compileResult.Plan;
        var execution = await _runtimeExecutor.ExecuteAsync(runtimePlan, cancellationToken);
        AddIssues(issues, execution.Issues);

        return new ThreeLayerExperimentRun
        {
            RunIndex = runIndex,
            Status = execution.Succeeded ? "executed" : "execution_failed",
            OutlineValid = true,
            LowLevelValid = true,
            RuntimeCompiled = true,
            RuntimeExecuted = execution.Succeeded,
            OutlineShape = ShapeSignatureBuilder.BuildOutlineShape(outlinePlan),
            LowLevelShape = ShapeSignatureBuilder.BuildLowLevelShape(lowLevelPlan),
            RuntimeShape = ShapeSignatureBuilder.BuildRuntimeShape(runtimePlan),
            FinalArtifactType = DetermineArtifactType(execution.FinalOutput),
            ResultStepId = runtimePlan.ResultStepId,
            OutlinePlanJson = ExperimentJson.SerializeIndented(outlinePlan),
            OutlineRawResponse = outlineStage.RawResponse,
            LowLevelPlanJson = ExperimentJson.SerializeIndented(lowLevelPlan),
            LowLevelRawResponse = lowLevelStage.RawResponse,
            RuntimePlanJson = ExperimentJson.SerializeIndented(runtimePlan),
            FinalOutputJson = execution.FinalOutput?.ToJsonString(ExperimentJson.SerializerOptions),
            Issues = issues
        };
    }

    private static string DetermineArtifactType(JsonNode? value) =>
        value switch
        {
            null => "null",
            JsonValue jsonValue when jsonValue.TryGetValue<string>(out _) => "string",
            _ => "json"
        };

    private static void AddIssues(List<string> target, IEnumerable<PlanningIssue> issues)
    {
        foreach (var issue in issues)
            target.Add($"{issue.Layer}:{issue.Code}:{issue.Message}");
    }
}
