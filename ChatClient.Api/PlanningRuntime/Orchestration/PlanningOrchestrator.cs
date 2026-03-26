using System.Text.Json;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Api.PlanningRuntime.Verification;

namespace ChatClient.Api.PlanningRuntime.Orchestration;

public sealed class PlanningOrchestrator(
    IPlanner planner,
    PlanExecutor executor,
    GoalVerifier? goalVerifier = null,
    IExecutionLogger? executionLogger = null,
    int maxAttempts = 3,
    IReplanner? replanner = null,
    IFinalAnswerVerifier? finalAnswerVerifier = null,
    IPlanRunObserver? planRunObserver = null)
{
    private readonly GoalVerifier _goalVerifier = goalVerifier ?? new GoalVerifier();
    private readonly IExecutionLogger _log = executionLogger ?? NullExecutionLogger.Instance;
    private readonly int _maxAttempts = maxAttempts;
    private readonly IReplanner? _replanner = replanner;
    private readonly IFinalAnswerVerifier? _finalAnswerVerifier = finalAnswerVerifier;
    private readonly IPlanRunObserver _observer = planRunObserver ?? NullPlanRunObserver.Instance;

    public async Task<ResultEnvelope<JsonElement?>> RunAsync(
        string userQuery,
        CancellationToken cancellationToken = default)
    {
        PlanDefinition? plan = null;
        PlannerReplanRequest? replanRequest = null;

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            _log.Log($"[orchestrator] attempt={attempt} phase={(plan is null ? "plan" : "replan")}");
            _observer.OnEvent(new PlanningAttemptStartedEvent(attempt, plan is null ? "plan" : "replan", userQuery));

            plan = plan is null
                ? PlanRuntimeHydrator.CreateRuntimePlan(await planner.CreatePlanAsync(userQuery, cancellationToken))
                : await CreateReplanAsync(replanRequest!, cancellationToken);

            var result = await executor.ExecuteAsync(plan, cancellationToken);
            var verdict = _goalVerifier.Check(plan, result);

            _log.Log($"[verify] execution:action={verdict.Action} reason={verdict.Reason}");
            _observer.OnEvent(new GoalVerifiedEvent(verdict));

            if (verdict.Action == GoalAction.Done)
            {
                var finalVerification = await VerifyFinalAnswerAsync(userQuery, plan.Steps[^1].Result, cancellationToken);
                if (finalVerification is null || finalVerification.IsAnswer)
                    return CompleteRun(ResultEnvelope<JsonElement?>.Success(plan.Steps[^1].Result?.Clone()), plan);

                verdict = new GoalVerdict
                {
                    Action = GoalAction.Replan,
                    Reason = finalVerification.Reason,
                    Missing = finalVerification.Missing.ToList()
                };
                _observer.OnEvent(new GoalVerifiedEvent(verdict));
                _log.Log($"[verify] answer:action={verdict.Action} reason={verdict.Reason}");
            }

            if (verdict.Action == GoalAction.AskUser)
            {
                return CompleteRun(ResultEnvelope<JsonElement?>.Failure(
                    "ask_user",
                    verdict.Reason,
                    JsonSerializer.SerializeToElement(new { question = verdict.UserQuestion })), plan);
            }

            if (verdict.Action == GoalAction.Blocked)
                return CompleteRun(BuildFinalFailureResult(plan, result, verdict, attempt), plan);

            if (attempt == _maxAttempts || _replanner is null)
            {
                return CompleteRun(BuildFinalFailureResult(plan, result, verdict, attempt), plan);
            }

            replanRequest = new PlannerReplanRequest
            {
                UserQuery = userQuery,
                AttemptNumber = attempt,
                Plan = plan,
                ExecutionResult = result,
                GoalVerdict = verdict
            };
        }

        return CompleteRun(ResultEnvelope<JsonElement?>.Failure("goal_not_achieved", "Plan execution exceeded max attempts."), plan);
    }

    private async Task<PlanDefinition> CreateReplanAsync(PlannerReplanRequest request, CancellationToken cancellationToken)
    {
        if (_replanner is null)
            return request.Plan;

        return await _replanner.ReplanAsync(request, cancellationToken);
    }

    private async Task<FinalAnswerVerificationResult?> VerifyFinalAnswerAsync(
        string userQuery,
        JsonElement? finalAnswer,
        CancellationToken cancellationToken)
    {
        if (_finalAnswerVerifier is null)
            return null;

        try
        {
            var verificationResult = await _finalAnswerVerifier.VerifyAsync(userQuery, finalAnswer, cancellationToken);
            _observer.OnEvent(new FinalAnswerVerifiedEvent(verificationResult));
            _log.Log($"[verify] answer:isAnswer={verificationResult.IsAnswer} reason={verificationResult.Reason}");
            return verificationResult;
        }
        catch (Exception ex)
        {
            _observer.OnEvent(new FinalAnswerVerificationFailedEvent(ex.GetType().Name, ex.Message));
            _log.Log($"[verify] answer:error={ex.GetType().Name} message={ex.Message}");
            return null;
        }
    }

    private ResultEnvelope<JsonElement?> CompleteRun(ResultEnvelope<JsonElement?> result, PlanDefinition? plan)
    {
        _observer.OnEvent(new RunCompletedEvent(result, plan is null ? null : ClonePlan(plan)));
        return result;
    }

    private static ResultEnvelope<JsonElement?> BuildFinalFailureResult(
        PlanDefinition? plan,
        ExecutionResult executionResult,
        GoalVerdict verdict,
        int attempt)
    {
        var failedSteps = plan?.Steps
            .Where(PlanExecutionState.IsFailed)
            .Select(step => new
            {
                id = step.Id,
                kind = step.Kind,
                capabilityId = step.CapabilityId,
                code = step.Error?.Code,
                message = step.Error?.Message
            })
            .ToList()
            ?? [];
        var partialSteps = plan?.Steps
            .Where(PlanExecutionState.IsPartial)
            .Select(step => new
            {
                id = step.Id,
                kind = step.Kind,
                capabilityId = step.CapabilityId,
                code = step.Error?.Code,
                message = step.Error?.Message
            })
            .ToList()
            ?? [];
        var completedStepIds = plan?.Steps
            .Where(PlanExecutionState.IsDone)
            .Select(step => step.Id)
            .ToList()
            ?? [];
        var lastAvailableStep = plan?.Steps.LastOrDefault(PlanExecutionState.HasCompletedResult);
        var hasPartialData = partialSteps.Count > 0;
        var isBlocked = verdict.Action == GoalAction.Blocked;
        var errorCode = isBlocked
            ? ResolveBlockedErrorCode(executionResult.LastEnvelope?.Error)
            : hasPartialData
                ? "partial_execution"
            : executionResult.LastEnvelope?.Error?.Code ?? "goal_not_achieved";
        var errorMessage = isBlocked
            ? verdict.Reason
            : hasPartialData
                ? "Execution completed with partial data: one or more aggregated steps failed for some inputs, so the result may be incomplete."
            : verdict.Reason;

        return ResultEnvelope<JsonElement?>.Failure(
            errorCode,
            errorMessage,
            JsonSerializer.SerializeToElement(new
            {
                attempt,
                reason = verdict.Reason,
                missing = verdict.Missing,
                hasPartialData,
                completedSteps = completedStepIds,
                partialSteps,
                failedSteps,
                lastAvailableStep = lastAvailableStep is null
                    ? null
                    : new
                    {
                        id = lastAvailableStep.Id,
                        status = lastAvailableStep.Status
                    },
                lastAvailableResult = lastAvailableStep?.Result?.Clone()
            }));
    }

    private static PlanDefinition ClonePlan(PlanDefinition plan) =>
        JsonSerializer.Deserialize<PlanDefinition>(JsonSerializer.Serialize(plan))
        ?? throw new InvalidOperationException("Failed to clone final plan.");

    private static string ResolveBlockedErrorCode(ErrorInfo? error)
    {
        if (string.Equals(error?.Code, "insufficient_capabilities", StringComparison.Ordinal))
            return "insufficient_capabilities";

        if (error?.Details is { ValueKind: JsonValueKind.Object } details
            && details.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "insufficient_capability", StringComparison.Ordinal))
        {
            return "insufficient_capabilities";
        }

        return error?.Code ?? "insufficient_capabilities";
    }
}

