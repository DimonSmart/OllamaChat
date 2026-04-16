using ChatClient.Api.PlanningRuntime.LowLevel;
using ChatClient.Api.PlanningRuntime.Shared;
using ChatClient.Api.Services;

namespace ChatClient.Api.PlanningRuntime.Runtime;

public sealed class RuntimePlanValidationResult
{
    public bool IsValid => Issues.Count == 0;

    public List<PlanningIssue> Issues { get; } = [];
}

public static class RuntimePlanValidator
{
    public static RuntimePlanValidationResult Validate(
        RuntimePlan plan,
        IReadOnlyCollection<AppToolDescriptor> tools)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(tools);

        var result = new RuntimePlanValidationResult();
        if (string.IsNullOrWhiteSpace(plan.Goal))
            result.Issues.Add(CreateIssue("goal_missing", "RuntimePlan.goal is required."));

        if (string.IsNullOrWhiteSpace(plan.ResultStepId))
            result.Issues.Add(CreateIssue("result_step_missing", "RuntimePlan.resultStepId is required."));

        if (string.IsNullOrWhiteSpace(plan.ResultPort))
            result.Issues.Add(CreateIssue("result_port_missing", "RuntimePlan.resultPort is required."));

        if (plan.Steps.Count == 0)
        {
            result.Issues.Add(CreateIssue("steps_empty", "RuntimePlan.steps must not be empty."));
            return result;
        }

        var toolIds = tools.Select(static tool => tool.QualifiedName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            if (string.IsNullOrWhiteSpace(step.Id) || !stepIds.Add(step.Id))
                result.Issues.Add(CreateIssue("step_id_invalid", $"Runtime step at index {index} must have a unique id."));

            if (string.Equals(step.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(step.CapabilityId)
                && !toolIds.Contains(step.CapabilityId))
            {
                result.Issues.Add(CreateIssue("capability_unknown", $"Runtime step '{step.Id}' references unknown capability '{step.CapabilityId}'."));
            }

            foreach (var input in step.In)
                ValidateInput(plan, step, input.Key, input.Value, index, result);
        }

        var resultSteps = plan.Steps.Where(static step => step.IsResult).ToList();
        if (resultSteps.Count != 1)
            result.Issues.Add(CreateIssue("result_step_count_invalid", "Exactly one runtime step must have isResult=true."));

        var runtimeResultStep = plan.Steps.FirstOrDefault(step => string.Equals(step.Id, plan.ResultStepId, StringComparison.OrdinalIgnoreCase));
        if (runtimeResultStep is null)
        {
            result.Issues.Add(CreateIssue("result_step_unknown", $"RuntimePlan.resultStepId '{plan.ResultStepId}' does not exist."));
            return result;
        }

        if (!runtimeResultStep.Outputs.Any(output => string.Equals(output.Name, plan.ResultPort, StringComparison.OrdinalIgnoreCase)))
            result.Issues.Add(CreateIssue("result_port_unknown", $"RuntimePlan.resultPort '{plan.ResultPort}' does not exist on step '{plan.ResultStepId}'."));

        foreach (var step in plan.Steps)
        {
            var hasDownstream = plan.Steps.Any(candidate =>
                candidate.In.Values.Any(input =>
                    string.Equals(input.From, $"${step.Id}.{plan.ResultPort}", StringComparison.OrdinalIgnoreCase)
                    || RuntimeBindingResolver.TryParseBindingPath(input.From ?? string.Empty, out var sourceStepId, out _) && string.Equals(sourceStepId, step.Id, StringComparison.OrdinalIgnoreCase)));

            if (!step.IsResult && !hasDownstream)
                result.Issues.Add(CreateIssue("unused_step", $"Runtime step '{step.Id}' has no downstream consumer."));
        }

        var terminalCount = plan.Steps.Count(step =>
            !plan.Steps.Any(candidate =>
                candidate.In.Values.Any(input =>
                    RuntimeBindingResolver.TryParseBindingPath(input.From ?? string.Empty, out var sourceStepId, out _)
                    && string.Equals(sourceStepId, step.Id, StringComparison.OrdinalIgnoreCase))));
        if (terminalCount != 1)
            result.Issues.Add(CreateIssue("terminal_step_count_invalid", $"Runtime plan must have exactly one terminal step, found {terminalCount}."));

        return result;
    }

    private static void ValidateInput(
        RuntimePlan plan,
        RuntimeStep step,
        string inputName,
        RuntimeInputValue input,
        int stepIndex,
        RuntimePlanValidationResult result)
    {
        if (string.Equals(input.Kind, RuntimeInputValueKinds.Literal, StringComparison.OrdinalIgnoreCase))
            return;

        if (!string.Equals(input.Kind, RuntimeInputValueKinds.Binding, StringComparison.OrdinalIgnoreCase))
        {
            result.Issues.Add(CreateIssue("input_kind_invalid", $"Runtime step '{step.Id}' input '{inputName}' has unsupported kind '{input.Kind}'."));
            return;
        }

        if (!RuntimeBindingResolver.TryParseBindingPath(input.From ?? string.Empty, out var sourceStepId, out var port))
        {
            result.Issues.Add(CreateIssue("binding_path_invalid", $"Runtime step '{step.Id}' input '{inputName}' has invalid binding path '{input.From}'."));
            return;
        }

        var sourceIndex = plan.Steps.FindIndex(candidate => string.Equals(candidate.Id, sourceStepId, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0)
        {
            result.Issues.Add(CreateIssue("binding_step_missing", $"Runtime step '{step.Id}' input '{inputName}' references unknown step '{sourceStepId}'."));
            return;
        }

        if (sourceIndex >= stepIndex)
        {
            result.Issues.Add(CreateIssue("binding_future_step", $"Runtime step '{step.Id}' input '{inputName}' references future step '{sourceStepId}'."));
            return;
        }

        var sourceStep = plan.Steps[sourceIndex];
        var sourcePort = sourceStep.Outputs.FirstOrDefault(output => string.Equals(output.Name, port, StringComparison.OrdinalIgnoreCase));
        if (sourcePort is null)
        {
            result.Issues.Add(CreateIssue("binding_port_missing", $"Runtime step '{step.Id}' input '{inputName}' references missing port '{port}' on step '{sourceStepId}'."));
            return;
        }

        if (string.Equals(input.Mode, LowLevelInputModes.Map, StringComparison.OrdinalIgnoreCase)
            && !sourcePort.SemanticType.EndsWith("[]", StringComparison.Ordinal))
        {
            result.Issues.Add(CreateIssue(
                "binding_map_non_array",
                $"Runtime step '{step.Id}' input '{inputName}' uses map mode on non-array port '{sourcePort.Name}'. The source output must be declared as an array semantic type."));
        }
    }

    private static PlanningIssue CreateIssue(string code, string message) =>
        new()
        {
            Code = code,
            Message = message,
            Layer = "runtime"
        };
}
