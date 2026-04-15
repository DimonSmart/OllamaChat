using ChatClient.Tests.Experiments.ThreeLayerPlanning.Contracts;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Outline;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Tools;

public sealed class OutlinePlanEditingTools
{
    private OutlinePlan _plan;

    public OutlinePlanEditingTools(OutlinePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _plan = Clone(plan);
    }

    public OutlinePlan ReadPlan() => Clone(_plan);

    public OutlineNode? ReadNode(string nodeId) =>
        _plan.Nodes.FirstOrDefault(node => string.Equals(node.Id, nodeId, StringComparison.OrdinalIgnoreCase));

    public void ReplaceNode(string nodeId, OutlineNode node)
    {
        var index = FindNodeIndex(nodeId);
        _plan.Nodes[index] = node;
    }

    public void AddNode(string? afterNodeId, OutlineNode node)
    {
        if (string.IsNullOrWhiteSpace(afterNodeId))
        {
            _plan.Nodes.Add(node);
            return;
        }

        var index = FindNodeIndex(afterNodeId);
        _plan.Nodes.Insert(index + 1, node);
    }

    public void RemoveNode(string nodeId)
    {
        var index = FindNodeIndex(nodeId);
        _plan.Nodes.RemoveAt(index);

        foreach (var node in _plan.Nodes)
        {
            node.DependsOn.RemoveAll(dependsOn => string.Equals(dependsOn, nodeId, StringComparison.OrdinalIgnoreCase));
            node.Inputs.RemoveAll(input => string.Equals(input.FromNodeId, nodeId, StringComparison.OrdinalIgnoreCase));
        }

        if (string.Equals(_plan.ResultNodeId, nodeId, StringComparison.OrdinalIgnoreCase))
            _plan = ReplaceResultNodeId(null);
    }

    public void LinkNodes(string fromNodeId, string toNodeId, string inputName, string semanticType)
    {
        var toNode = _plan.Nodes[FindNodeIndex(toNodeId)];
        if (!toNode.DependsOn.Contains(fromNodeId, StringComparer.OrdinalIgnoreCase))
            toNode.DependsOn.Add(fromNodeId);

        toNode.Inputs.RemoveAll(input => string.Equals(input.Name, inputName, StringComparison.OrdinalIgnoreCase));
        toNode.Inputs.Add(new OutlineNodeInput
        {
            Name = inputName,
            SemanticType = semanticType,
            FromNodeId = fromNodeId
        });
    }

    public void MarkResultNode(string nodeId)
    {
        FindNodeIndex(nodeId);
        _plan = ReplaceResultNodeId(nodeId);
    }

    public OutlineValidationResult Validate() =>
        OutlineValidator.Validate(_plan);

    private OutlinePlan ReplaceResultNodeId(string? resultNodeId) =>
        new()
        {
            Goal = _plan.Goal,
            BlockedReason = _plan.BlockedReason,
            ResultNodeId = resultNodeId,
            RequiredDeliverables = [.. _plan.RequiredDeliverables],
            Nodes = [.. _plan.Nodes]
        };

    private static OutlinePlan Clone(OutlinePlan plan) =>
        ExperimentJson.DeserializeNode<OutlinePlan>(ExperimentJson.ToNode(plan));

    private int FindNodeIndex(string nodeId)
    {
        var index = _plan.Nodes.FindIndex(node => string.Equals(node.Id, nodeId, StringComparison.OrdinalIgnoreCase));
        return index >= 0
            ? index
            : throw new InvalidOperationException($"Outline node '{nodeId}' was not found.");
    }
}
