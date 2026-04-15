using ChatClient.Api.Services;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Contracts;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.LowLevel;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Tools;

public sealed class LowLevelPlanEditingTools
{
    private LowLevelPlan _plan;
    private readonly OutlinePlan _outlinePlan;
    private readonly IReadOnlyCollection<AppToolDescriptor> _tools;

    public LowLevelPlanEditingTools(
        LowLevelPlan plan,
        OutlinePlan outlinePlan,
        IReadOnlyCollection<AppToolDescriptor> tools)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outlinePlan);
        ArgumentNullException.ThrowIfNull(tools);

        _plan = Clone(plan);
        _outlinePlan = outlinePlan;
        _tools = tools;
    }

    public LowLevelPlan ReadPlan() => Clone(_plan);

    public LowLevelStep? ReadStep(string stepId) =>
        _plan.Steps.FirstOrDefault(step => string.Equals(step.Id, stepId, StringComparison.OrdinalIgnoreCase));

    public void ReplaceStep(string stepId, LowLevelStep step)
    {
        var index = FindStepIndex(stepId);
        _plan.Steps[index] = step;
    }

    public void AddStep(string? afterStepId, LowLevelStep step)
    {
        if (string.IsNullOrWhiteSpace(afterStepId))
        {
            _plan.Steps.Add(step);
            return;
        }

        var index = FindStepIndex(afterStepId);
        _plan.Steps.Insert(index + 1, step);
    }

    public void RemoveStep(string stepId)
    {
        var index = FindStepIndex(stepId);
        _plan.Steps.RemoveAt(index);

        foreach (var step in _plan.Steps)
        {
            step.Inputs.RemoveAll(input => string.Equals(input.Source.StepId, stepId, StringComparison.OrdinalIgnoreCase));
        }

        if (string.Equals(_plan.ResultStepId, stepId, StringComparison.OrdinalIgnoreCase))
            _plan = ReplaceResultStep(null);
    }

    public void RewireInput(string stepId, string inputName, LowLevelInputSource source)
    {
        var step = _plan.Steps[FindStepIndex(stepId)];
        var existing = step.Inputs.FirstOrDefault(candidate => string.Equals(candidate.Name, inputName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            step.Inputs.Remove(existing);

        step.Inputs.Add(new LowLevelStepInput
        {
            Name = inputName,
            Source = source
        });
    }

    public void MarkResultStep(string stepId)
    {
        FindStepIndex(stepId);
        _plan = ReplaceResultStep(stepId);
    }

    public IReadOnlyList<string> ListPorts(string stepId)
    {
        var step = _plan.Steps[FindStepIndex(stepId)];
        return step.Outputs.Select(static output => output.Name).ToList();
    }

    public LowLevelValidationResult Validate() =>
        LowLevelValidator.Validate(_plan, _outlinePlan, _tools);

    private LowLevelPlan ReplaceResultStep(string? resultStepId) =>
        new()
        {
            Goal = _plan.Goal,
            BlockedReason = _plan.BlockedReason,
            OutlineResultNodeId = _plan.OutlineResultNodeId,
            ResultStepId = resultStepId,
            Steps = _plan.Steps.Select(step => new LowLevelStep
            {
                Id = step.Id,
                OutlineNodeId = step.OutlineNodeId,
                Kind = step.Kind,
                CapabilityId = step.CapabilityId,
                Purpose = step.Purpose,
                Inputs = step.Inputs,
                Outputs = step.Outputs,
                Fanout = step.Fanout,
                Out = step.Out,
                IsResult = !string.IsNullOrWhiteSpace(resultStepId)
                    && string.Equals(step.Id, resultStepId, StringComparison.OrdinalIgnoreCase)
            }).ToList()
        };

    private static LowLevelPlan Clone(LowLevelPlan plan) =>
        ExperimentJson.DeserializeNode<LowLevelPlan>(ExperimentJson.ToNode(plan));

    private int FindStepIndex(string stepId)
    {
        var index = _plan.Steps.FindIndex(step => string.Equals(step.Id, stepId, StringComparison.OrdinalIgnoreCase));
        return index >= 0
            ? index
            : throw new InvalidOperationException($"Low-level step '{stepId}' was not found.");
    }
}
