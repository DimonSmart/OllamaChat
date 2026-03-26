using System.Text.Json;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Planning;

namespace ChatClient.Api.PlanningRuntime.Verification;

public sealed class GoalVerifier(bool askUserEnabled = false)
{
    public GoalVerdict Check(PlanDefinition plan, ExecutionResult executionResult)
    {
        var verificationIssues = plan.Steps
            .Where(step => string.Equals(step.Error?.Code, "verification_failed", StringComparison.Ordinal))
            .SelectMany(step => ExtractVerificationIssues(step.Id, step.Error?.Details))
            .ToList();

        if (verificationIssues.Count > 0)
        {
            return new GoalVerdict
            {
                Action = GoalAction.Replan,
                Reason = "Execution completed, but one or more step outputs look incomplete.",
                Missing = verificationIssues
            };
        }

        var failedSteps = plan.Steps.Where(PlanExecutionState.IsFailed).ToList();
        var blockedFailure = failedSteps
            .Select(step => new
            {
                Step = step,
                Details = TryGetFailureDetails(step.Error?.Details)
            })
            .FirstOrDefault(candidate =>
                candidate.Details is not null
                && (!candidate.Details.NeedsReplan
                    || string.Equals(candidate.Details.Type, "insufficient_capability", StringComparison.Ordinal)));
        if (blockedFailure is not null)
        {
            return new GoalVerdict
            {
                Action = GoalAction.Blocked,
                Reason = blockedFailure.Step.Error?.Message
                    ?? "Execution is blocked because the available capabilities are insufficient.",
                Missing = blockedFailure.Details?.Details?.ToList() ?? [blockedFailure.Step.Id]
            };
        }

        if (failedSteps.Count > 0)
        {
            return new GoalVerdict
            {
                Action = GoalAction.Replan,
                Reason = "Execution has failed steps.",
                Missing = failedSteps.Select(step => step.Id).ToList()
            };
        }

        var finalStep = plan.Steps[^1];
        var terminalStepIds = PlanDependencyGraph.GetTerminalStepIds(plan.Steps);
        var orphanTerminalStepIds = terminalStepIds
            .Where(stepId => !string.Equals(stepId, finalStep.Id, StringComparison.Ordinal))
            .ToList();
        if (orphanTerminalStepIds.Count > 0 || terminalStepIds.Count != 1)
        {
            return new GoalVerdict
            {
                Action = GoalAction.Replan,
                Reason = orphanTerminalStepIds.Count > 0
                    ? "Plan contains terminal steps whose results are never consumed by the final step."
                    : $"Plan must end with exactly one terminal step, but found {terminalStepIds.Count}.",
                Missing = orphanTerminalStepIds.Count > 0
                    ? orphanTerminalStepIds
                    : terminalStepIds.ToList()
            };
        }

        if (!PlanExecutionState.HasCompletedResult(finalStep))
        {
            return new GoalVerdict
            {
                Action = GoalAction.Replan,
                Reason = $"Plan does not contain a completed final result for step '{finalStep.Id}'.",
                Missing = [finalStep.Id]
            };
        }

        var finalNode = finalStep.Result!.Value;
        if (finalNode.ValueKind == JsonValueKind.Object && !finalNode.EnumerateObject().Any())
        {
            return new GoalVerdict
            {
                Action = GoalAction.Replan,
                Reason = "Final output is an empty object.",
                Missing = ["final_content"]
            };
        }

        if (finalNode.ValueKind == JsonValueKind.Array && !finalNode.EnumerateArray().Any())
        {
            return new GoalVerdict
            {
                Action = GoalAction.Replan,
                Reason = "Final output is an empty array.",
                Missing = ["final_content"]
            };
        }

        if (finalNode.ValueKind == JsonValueKind.String
            && string.IsNullOrWhiteSpace(finalNode.GetString()))
        {
            return new GoalVerdict
            {
                Action = GoalAction.Replan,
                Reason = "Final output is an empty string.",
                Missing = ["final_content"]
            };
        }

        if (askUserEnabled
            && finalNode.ValueKind == JsonValueKind.Object
            && finalNode.TryGetProperty("needUserInput", out var needUserInput)
            && needUserInput.ValueKind == JsonValueKind.True)
        {
            return new GoalVerdict
            {
                Action = GoalAction.AskUser,
                Reason = "Need clarification from user.",
                UserQuestion = finalNode.TryGetProperty("question", out var question)
                    && question.ValueKind == JsonValueKind.String
                    ? question.GetString()
                    : null
            };
        }

        return new GoalVerdict
        {
            Action = GoalAction.Done,
            Reason = PlanExecutionState.IsPartial(finalStep)
                ? "Execution produced a partial final result."
                : "Execution produced a non-empty final result."
        };
    }

    private static IReadOnlyCollection<string> ExtractVerificationIssues(string stepId, JsonElement? errorDetails)
    {
        if (errorDetails is not { ValueKind: JsonValueKind.Object } details
            || !details.TryGetProperty("issues", out var issues)
            || issues.ValueKind != JsonValueKind.Array)
        {
            return [$"{stepId}:verification_failed"];
        }

        return issues
            .EnumerateArray()
            .Select(issue =>
                issue.ValueKind == JsonValueKind.Object
                && issue.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                    ? code.GetString()
                    : null)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => $"{stepId}:{code}")
            .ToList();
    }

    private static LlmFailureDetails? TryGetFailureDetails(JsonElement? details)
    {
        if (details is not { ValueKind: JsonValueKind.Object } objectDetails)
            return null;

        try
        {
            return objectDetails.Deserialize<LlmFailureDetails>();
        }
        catch
        {
            return null;
        }
    }
}
