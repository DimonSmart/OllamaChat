using ChatClient.Api.Services;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Tools;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.LowLevel;

public sealed class LowLevelRepairer
{
    public Contracts.LowLevelPlan Repair(
        Contracts.LowLevelPlan plan,
        Contracts.OutlinePlan outlinePlan,
        IReadOnlyCollection<AppToolDescriptor> tools,
        IReadOnlyList<ExperimentIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outlinePlan);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(issues);

        var editor = new LowLevelPlanEditingTools(plan, outlinePlan, tools);
        foreach (var issue in issues)
        {
            switch (issue.Code)
            {
                case "result_step_missing":
                case "result_step_unknown":
                case "result_step_count_invalid":
                case "result_step_non_terminal":
                    MarkSingleTerminalStepAsResult(editor);
                    break;
                case "unused_step":
                    RemoveUnusedTerminalSteps(editor);
                    break;
            }
        }

        return editor.ReadPlan();
    }

    private static void MarkSingleTerminalStepAsResult(LowLevelPlanEditingTools editor)
    {
        var plan = editor.ReadPlan();
        var terminals = plan.Steps
            .Where(step => !plan.Steps.Any(candidate =>
                candidate.Inputs.Any(input => string.Equals(input.Source.StepId, step.Id, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        if (terminals.Count == 1)
            editor.MarkResultStep(terminals[0].Id);
    }

    private static void RemoveUnusedTerminalSteps(LowLevelPlanEditingTools editor)
    {
        var plan = editor.ReadPlan();
        foreach (var step in plan.Steps.ToList())
        {
            if (step.IsResult)
                continue;

            var hasDownstream = plan.Steps.Any(candidate =>
                candidate.Inputs.Any(input => string.Equals(input.Source.StepId, step.Id, StringComparison.OrdinalIgnoreCase)));
            if (!hasDownstream)
                editor.RemoveStep(step.Id);
        }
    }
}
