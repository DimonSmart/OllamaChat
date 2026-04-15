using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.LowLevel;
using ChatClient.Api.PlanningRuntime.Outline;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Runtime;
using ChatClient.Api.PlanningRuntime.Shared;
using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using System.Text.Json;

namespace ChatClient.Api.PlanningRuntime.Host;

public interface IPlanningRunExecutor
{
    Task<ResultEnvelope<JsonElement?>> ExecuteAsync(
        PlanningRunExecutionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class PlanningRunExecutor(
    ILlmChatClientFactory chatClientFactory,
    IMcpUserInteractionService mcpUserInteractionService) : IPlanningRunExecutor
{
    public async Task<ResultEnvelope<JsonElement?>> ExecuteAsync(
        PlanningRunExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var chatClient = await chatClientFactory.CreateAsync(request.Model, cancellationToken);
        var observer = request.PlanRunObserver ?? NullPlanRunObserver.Instance;
        var loggerSink = request.ExecutionLogger ?? NullExecutionLogger.Instance;
        var requestAnalyzer = new LlmPlanningRequestAnalyzer(chatClient, loggerSink, observer);
        var llmClient = new ChatClientPlanningLlmClient(chatClient);
        var capabilities = CapabilitySummaryBuilder.Build(request.EnabledTools);

        observer.OnEvent(new PlanningAttemptStartedEvent(1, "three_layer", request.UserQuery));
        loggerSink.Log($"[run] capabilities tools={capabilities.Count} ids=[{string.Join(",", capabilities.Select(static c => c.ToolId))}]");

        try
        {
            var brief = await requestAnalyzer.AnalyzeAsync(request.UserQuery, cancellationToken);
            var outlineResult = await RunOutlineStageAsync(llmClient, capabilities, brief, observer, loggerSink, cancellationToken);
            if (!outlineResult.Ok)
            {
                observer.OnEvent(new RunCompletedEvent(outlineResult, null));
                return outlineResult;
            }

            if (outlineResult.Data is not JsonElement outlineElement)
                throw new InvalidOperationException("Outline stage returned no plan.");
            var outlinePlan = outlineElement.Deserialize<OutlinePlan>(PlanningNodeJson.SerializerOptions)
                ?? throw new InvalidOperationException("Outline stage returned no plan.");
            var lowLevelResult = await RunLowLevelStageAsync(llmClient, capabilities, request.EnabledTools, outlinePlan, observer, loggerSink, cancellationToken);
            if (!lowLevelResult.Ok)
            {
                observer.OnEvent(new RunCompletedEvent(lowLevelResult, null));
                return lowLevelResult;
            }

            if (lowLevelResult.Data is not JsonElement lowLevelElement)
                throw new InvalidOperationException("Low-level stage returned no plan.");
            var lowLevelPlan = lowLevelElement.Deserialize<LowLevelPlan>(PlanningNodeJson.SerializerOptions)
                ?? throw new InvalidOperationException("Low-level stage returned no plan.");
            var compiler = new RuntimePlannerCompiler(request.EnabledTools);
            var compileResult = compiler.Compile(lowLevelPlan);
            observer.OnEvent(new RuntimeCompilationCompletedEvent(
                compileResult.Plan,
                compileResult.Issues,
                compileResult.IsSuccess));

            if (!compileResult.IsSuccess || compileResult.Plan is null)
            {
                foreach (var issue in compileResult.Issues)
                    loggerSink.Log($"[runtime] compile issue {issue.Code}: {issue.Message}");

                var failure = CreateFailure("runtime_compile_failed", "Runtime plan compilation failed.", compileResult.Issues);
                observer.OnEvent(new RunCompletedEvent(failure, null));
                return failure;
            }

            var runtimeExecutor = new RuntimePlanExecutor(
                llmClient,
                request.EnabledTools,
                loggerSink,
                observer,
                mcpUserInteractionService);
            var execution = await runtimeExecutor.ExecuteAsync(compileResult.Plan, cancellationToken);
            foreach (var issue in execution.Issues)
                loggerSink.Log($"[runtime] execution issue {issue.Code}: {issue.Message}");

            var result = execution.Succeeded
                ? ResultEnvelope<JsonElement?>.Success(
                    execution.FinalOutput is null
                        ? null
                        : PlanningNodeJson.ToElement(execution.FinalOutput))
                : CreateFailure("runtime_execution_failed", "Runtime plan execution failed.", execution.Issues);
            observer.OnEvent(new RunCompletedEvent(result, null));
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            loggerSink.Log($"[planning] fatal error={ex.Message}");
            var failure = ResultEnvelope<JsonElement?>.Failure("planning_failed", ex.Message);
            observer.OnEvent(new RunCompletedEvent(failure, null));
            return failure;
        }
    }

    private static async Task<ResultEnvelope<JsonElement?>> RunOutlineStageAsync(
        IPlanningLlmClient llmClient,
        IReadOnlyCollection<CompactCapabilitySummary> capabilities,
        RequestBrief brief,
        IPlanRunObserver observer,
        IExecutionLogger loggerSink,
        CancellationToken cancellationToken)
    {
        var planner = new OutlinePlanner(llmClient);
        var repairer = new OutlineRepairer();

        try
        {
            var stage = await planner.CreatePlanAsync(
                new OutlinePlanningRequest
                {
                    UserQuery = string.IsNullOrWhiteSpace(brief.RewrittenRequest) ? brief.Goal : brief.RewrittenRequest,
                    ResultExpectations = BuildResultExpectations(brief),
                    Capabilities = capabilities
                },
                cancellationToken);

            var plan = stage.Plan;
            var validation = OutlineValidator.Validate(plan);
            if (!validation.IsValid)
            {
                plan = repairer.Repair(plan, validation.Issues);
                validation = OutlineValidator.Validate(plan);
            }

            observer.OnEvent(new OutlineStageCompletedEvent(plan, stage.RawResponse, validation.Issues, validation.IsValid));
            loggerSink.Log($"[outline] valid={validation.IsValid} nodes={plan.Nodes.Count}");

            if (!validation.IsValid)
                return CreateFailure("invalid_outline", "Outline plan validation failed.", validation.Issues);

            if (plan.IsBlocked)
                return CreateFailure("outline_blocked", plan.BlockedReason ?? "Outline planner returned a blocked plan.");

            return ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(plan, PlanningNodeJson.SerializerOptions));
        }
        catch (PlanningContractException ex) when (string.Equals(ex.Stage, "outline", StringComparison.OrdinalIgnoreCase))
        {
            var issues = ex.ContractIssues
                .Select(issue => new PlanningIssue
                {
                    Layer = "outline_contract",
                    Code = "contract_invalid",
                    Message = issue
                })
                .ToList();
            observer.OnEvent(new OutlineStageCompletedEvent(null, ex.RawResponse, issues, false));
            return CreateFailure("outline_contract_failed", "Outline planner returned an invalid contract.", issues);
        }
    }

    private static async Task<ResultEnvelope<JsonElement?>> RunLowLevelStageAsync(
        IPlanningLlmClient llmClient,
        IReadOnlyCollection<CompactCapabilitySummary> capabilities,
        IReadOnlyCollection<AppToolDescriptor> tools,
        OutlinePlan outlinePlan,
        IPlanRunObserver observer,
        IExecutionLogger loggerSink,
        CancellationToken cancellationToken)
    {
        var planner = new LowLevelPlanner(llmClient);
        var repairer = new LowLevelRepairer();

        try
        {
            var stage = await planner.CreatePlanAsync(
                new LowLevelPlanningRequest
                {
                    OutlinePlan = outlinePlan,
                    Capabilities = capabilities
                },
                cancellationToken);

            var plan = stage.Plan;
            var validation = LowLevelValidator.Validate(plan, outlinePlan, tools);
            if (!validation.IsValid)
            {
                plan = repairer.Repair(plan, outlinePlan, tools, validation.Issues);
                validation = LowLevelValidator.Validate(plan, outlinePlan, tools);
            }

            observer.OnEvent(new LowLevelStageCompletedEvent(plan, stage.RawResponse, validation.Issues, validation.IsValid));
            loggerSink.Log($"[low-level] valid={validation.IsValid} steps={plan.Steps.Count}");

            if (!validation.IsValid)
                return CreateFailure("invalid_low_level", "Low-level plan validation failed.", validation.Issues);

            if (plan.IsBlocked)
                return CreateFailure("low_level_blocked", plan.BlockedReason ?? "Low-level planner returned a blocked plan.");

            return ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(plan, PlanningNodeJson.SerializerOptions));
        }
        catch (PlanningContractException ex) when (string.Equals(ex.Stage, "low_level", StringComparison.OrdinalIgnoreCase))
        {
            var issues = ex.ContractIssues
                .Select(issue => new PlanningIssue
                {
                    Layer = "low_level_contract",
                    Code = "contract_invalid",
                    Message = issue
                })
                .ToList();
            observer.OnEvent(new LowLevelStageCompletedEvent(null, ex.RawResponse, issues, false));
            return CreateFailure("low_level_contract_failed", "Low-level planner returned an invalid contract.", issues);
        }
    }

    private static string BuildResultExpectations(RequestBrief brief)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(brief.ExpectedResult))
            lines.Add($"Expected result: {brief.ExpectedResult.Trim()}");
        if (brief.Deliverables.Count > 0)
            lines.Add($"Deliverables: {string.Join("; ", brief.Deliverables)}");
        if (brief.EvidenceRequirements.Count > 0)
            lines.Add($"Evidence: {string.Join("; ", brief.EvidenceRequirements)}");
        if (brief.SuccessCriteria.Count > 0)
            lines.Add($"Success criteria: {string.Join("; ", brief.SuccessCriteria)}");
        if (!string.IsNullOrWhiteSpace(brief.OutputExpectations))
            lines.Add($"Output expectations: {brief.OutputExpectations.Trim()}");

        return lines.Count == 0
            ? brief.Goal
            : string.Join(Environment.NewLine, lines);
    }

    private static ResultEnvelope<JsonElement?> CreateFailure(
        string code,
        string message,
        IReadOnlyCollection<PlanningIssue>? issues = null)
    {
        if (issues is null || issues.Count == 0)
            return ResultEnvelope<JsonElement?>.Failure(code, message);

        var details = JsonSerializer.SerializeToElement(new
        {
            issues = issues.Select(issue => new
            {
                layer = issue.Layer,
                code = issue.Code,
                message = issue.Message
            })
        });
        return ResultEnvelope<JsonElement?>.Failure(code, message, details);
    }
}

public sealed class PlanningRunExecutionRequest
{
    public required string UserQuery { get; init; }

    public required ServerModel Model { get; init; }

    public required IReadOnlyCollection<AppToolDescriptor> EnabledTools { get; init; }

    public IExecutionLogger? ExecutionLogger { get; init; }

    public IPlanRunObserver? PlanRunObserver { get; init; }
}
