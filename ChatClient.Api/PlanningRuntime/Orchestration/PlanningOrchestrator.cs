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
                ? await planner.CreatePlanAsync(userQuery, cancellationToken)
                : await CreateReplanAsync(replanRequest!, cancellationToken);

            var result = await executor.ExecuteAsync(plan, cancellationToken);
            var verdict = _goalVerifier.Check(plan, result);

            _log.Log($"[verify] goal:action={verdict.Action} reason={verdict.Reason}");
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
                _log.Log($"[verify] goal:action={verdict.Action} reason={verdict.Reason}");
            }

            if (verdict.Action == GoalAction.AskUser)
            {
                return CompleteRun(ResultEnvelope<JsonElement?>.Failure(
                    "ask_user",
                    verdict.Reason,
                    JsonSerializer.SerializeToElement(new { question = verdict.UserQuestion })), plan);
            }

            if (attempt == _maxAttempts || _replanner is null)
            {
                return CompleteRun(ResultEnvelope<JsonElement?>.Failure(
                    result.LastEnvelope?.Error?.Code ?? "goal_not_achieved",
                    verdict.Reason,
                    JsonSerializer.SerializeToElement(new
                    {
                        missing = verdict.Missing,
                        attempt
                    })), plan);
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

        var verificationResult = await _finalAnswerVerifier.VerifyAsync(userQuery, finalAnswer, cancellationToken);
        _observer.OnEvent(new FinalAnswerVerifiedEvent(verificationResult));
        _log.Log($"[verify] final:isAnswer={verificationResult.IsAnswer} reason={verificationResult.Reason}");
        return verificationResult;
    }

    private ResultEnvelope<JsonElement?> CompleteRun(ResultEnvelope<JsonElement?> result, PlanDefinition? plan)
    {
        _observer.OnEvent(new RunCompletedEvent(result, plan is null ? null : ClonePlan(plan)));
        return result;
    }

    private static PlanDefinition ClonePlan(PlanDefinition plan) =>
        JsonSerializer.Deserialize<PlanDefinition>(JsonSerializer.Serialize(plan))
        ?? throw new InvalidOperationException("Failed to clone final plan.");
}

