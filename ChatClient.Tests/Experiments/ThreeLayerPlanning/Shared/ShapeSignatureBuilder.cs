using ChatClient.Api.PlanningRuntime.LowLevel;
using ChatClient.Api.PlanningRuntime.Outline;
using ChatClient.Api.PlanningRuntime.Runtime;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;

public static class ShapeSignatureBuilder
{
    public static string BuildOutlineShape(OutlinePlan plan) =>
        string.Join("->", plan.Nodes.Select(static node => node.Kind));

    public static string BuildLowLevelShape(LowLevelPlan plan) =>
        string.Join(
            "->",
            plan.Steps.Select(static step =>
                string.IsNullOrWhiteSpace(step.CapabilityId)
                    ? $"{step.Kind}:{step.OutlineNodeId}"
                    : $"{step.Kind}:{step.CapabilityId}"));

    public static string BuildRuntimeShape(RuntimePlan plan) =>
        string.Join(
            "->",
            plan.Steps.Select(static step =>
                string.IsNullOrWhiteSpace(step.CapabilityId)
                    ? step.Kind
                    : $"{step.Kind}:{step.CapabilityId}"));
}
