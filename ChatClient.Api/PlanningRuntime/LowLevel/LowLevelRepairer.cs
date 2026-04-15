using ChatClient.Api.PlanningRuntime.Outline;
using ChatClient.Api.PlanningRuntime.Shared;
using ChatClient.Api.Services;

namespace ChatClient.Api.PlanningRuntime.LowLevel;

public sealed class LowLevelRepairer
{
    public LowLevelPlan Repair(
        LowLevelPlan plan,
        OutlinePlan outlinePlan,
        IReadOnlyCollection<AppToolDescriptor> tools,
        IReadOnlyList<PlanningIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outlinePlan);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(issues);

        var workingPlan = Clone(plan);
        foreach (var issue in issues)
        {
            switch (issue.Code)
            {
                case "result_step_missing":
                case "result_step_unknown":
                case "result_step_count_invalid":
                case "result_step_non_terminal":
                    workingPlan = MarkSingleTerminalStepAsResult(workingPlan);
                    break;
                case "unused_step":
                    workingPlan = RemoveUnusedTerminalSteps(workingPlan);
                    break;
            }
        }

        return workingPlan;
    }

    private static LowLevelPlan MarkSingleTerminalStepAsResult(LowLevelPlan plan)
    {
        var terminals = plan.Steps
            .Where(step => !plan.Steps.Any(candidate =>
                candidate.Inputs.Any(input => string.Equals(input.Source.StepId, step.Id, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        if (terminals.Count != 1)
            return plan;

        return new LowLevelPlan
        {
            Goal = plan.Goal,
            BlockedReason = plan.BlockedReason,
            OutlineResultNodeId = plan.OutlineResultNodeId,
            ResultStepId = terminals[0].Id,
            Steps = [.. plan.Steps.Select(step => CloneStep(step, string.Equals(step.Id, terminals[0].Id, StringComparison.OrdinalIgnoreCase)))]
        };
    }

    private static LowLevelPlan RemoveUnusedTerminalSteps(LowLevelPlan plan)
    {
        var filteredSteps = plan.Steps
            .Where(step =>
                step.IsResult
                || plan.Steps.Any(candidate =>
                    candidate.Inputs.Any(input => string.Equals(input.Source.StepId, step.Id, StringComparison.OrdinalIgnoreCase))))
            .Select(static step => CloneStep(step, step.IsResult))
            .ToList();

        return new LowLevelPlan
        {
            Goal = plan.Goal,
            BlockedReason = plan.BlockedReason,
            OutlineResultNodeId = plan.OutlineResultNodeId,
            ResultStepId = plan.ResultStepId,
            Steps = filteredSteps
        };
    }

    private static LowLevelPlan Clone(LowLevelPlan plan) =>
        new()
        {
            Goal = plan.Goal,
            BlockedReason = plan.BlockedReason,
            OutlineResultNodeId = plan.OutlineResultNodeId,
            ResultStepId = plan.ResultStepId,
            Steps = [.. plan.Steps.Select(static step => CloneStep(step, step.IsResult))]
        };

    private static LowLevelStep CloneStep(LowLevelStep step, bool isResult) =>
        new()
        {
            Id = step.Id,
            OutlineNodeId = step.OutlineNodeId,
            Kind = step.Kind,
            CapabilityId = step.CapabilityId,
            Purpose = step.Purpose,
            Inputs = [.. step.Inputs.Select(static input => new LowLevelStepInput
            {
                Name = input.Name,
                Source = new LowLevelInputSource
                {
                    Kind = input.Source.Kind,
                    Value = PlanningNodeJson.CloneNode(input.Source.Value),
                    StepId = input.Source.StepId,
                    Port = input.Source.Port,
                    Mode = input.Source.Mode
                }
            })],
            Outputs = [.. step.Outputs.Select(static output => new LowLevelStepOutput
            {
                Name = output.Name,
                SemanticType = output.SemanticType
            })],
            Fanout = step.Fanout,
            Out = step.Out is null
                ? null
                : new LowLevelStepOutputSettings
                {
                    Format = step.Out.Format
                },
            IsResult = isResult
        };
}
