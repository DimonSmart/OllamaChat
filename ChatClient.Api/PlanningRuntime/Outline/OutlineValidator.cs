using ChatClient.Api.PlanningRuntime.Shared;

namespace ChatClient.Api.PlanningRuntime.Outline;

public sealed class OutlineValidationResult
{
    public bool IsValid => Issues.Count == 0;

    public List<PlanningIssue> Issues { get; } = [];
}

public static class OutlineValidator
{
    public static OutlineValidationResult Validate(OutlinePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var result = new OutlineValidationResult();
        if (string.IsNullOrWhiteSpace(plan.Goal))
            result.Issues.Add(CreateIssue("goal_missing", "OutlinePlan.goal is required."));

        if (plan.Nodes.Count == 0)
        {
            result.Issues.Add(CreateIssue("nodes_empty", "OutlinePlan.nodes must not be empty."));
            return result;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < plan.Nodes.Count; index++)
        {
            var node = plan.Nodes[index];
            if (string.IsNullOrWhiteSpace(node.Id) || !ids.Add(node.Id))
                result.Issues.Add(CreateIssue("node_id_invalid", $"Outline node at index {index} must have a unique id."));

            if (!OutlineNodeKinds.All.Contains(node.Kind))
                result.Issues.Add(CreateIssue("node_kind_invalid", $"Outline node '{node.Id}' has unsupported kind '{node.Kind}'."));

            foreach (var dependsOn in node.DependsOn)
            {
                var dependsOnIndex = plan.Nodes.FindIndex(candidate => string.Equals(candidate.Id, dependsOn, StringComparison.OrdinalIgnoreCase));
                if (dependsOnIndex < 0)
                {
                    result.Issues.Add(CreateIssue("dependency_missing", $"Outline node '{node.Id}' depends on unknown node '{dependsOn}'."));
                    continue;
                }

                if (dependsOnIndex >= index)
                    result.Issues.Add(CreateIssue("dependency_future", $"Outline node '{node.Id}' depends on future node '{dependsOn}'."));
            }
        }

        if (plan.IsBlocked)
            return result;

        if (string.IsNullOrWhiteSpace(plan.ResultNodeId))
        {
            result.Issues.Add(CreateIssue("result_node_missing", "OutlinePlan.resultNodeId is required."));
            return result;
        }

        var resultNode = plan.Nodes.FirstOrDefault(node => string.Equals(node.Id, plan.ResultNodeId, StringComparison.OrdinalIgnoreCase));
        if (resultNode is null)
        {
            result.Issues.Add(CreateIssue("result_node_unknown", $"OutlinePlan.resultNodeId '{plan.ResultNodeId}' does not exist."));
            return result;
        }

        if (!string.Equals(resultNode.Kind, OutlineNodeKinds.Answer, StringComparison.OrdinalIgnoreCase))
        {
            result.Issues.Add(CreateIssue(
                "result_node_kind_invalid",
                $"Result node '{resultNode.Id}' must use kind '{OutlineNodeKinds.Answer}' so the low-level materialization has an unambiguous terminal answer step."));
        }

        var downstreamByNodeId = BuildDownstreamMap(plan.Nodes);
        if (downstreamByNodeId[resultNode.Id].Count != 0)
            result.Issues.Add(CreateIssue("result_node_non_terminal", $"Result node '{resultNode.Id}' must be terminal."));

        foreach (var node in plan.Nodes)
        {
            if (string.Equals(node.Id, resultNode.Id, StringComparison.OrdinalIgnoreCase))
                continue;

            if (downstreamByNodeId[node.Id].Count == 0)
                result.Issues.Add(CreateIssue("non_result_node_unused", $"Non-result node '{node.Id}' has no downstream consumer."));
        }

        var reachable = CollectAncestors(resultNode.Id, plan.Nodes);
        foreach (var node in plan.Nodes)
        {
            if (!reachable.Contains(node.Id))
                result.Issues.Add(CreateIssue("disconnected_subgraph", $"Outline node '{node.Id}' is disconnected from the result node."));

            var contract = OutlineNodeExecutionContractResolver.Resolve(node.Kind);
            if (contract.RequiresTerminalResult && !string.Equals(node.Id, resultNode.Id, StringComparison.OrdinalIgnoreCase))
            {
                result.Issues.Add(CreateIssue(
                    "node_kind_requires_terminal",
                    $"Outline node '{node.Id}' uses kind '{node.Kind}', but only the terminal result node may use that kind."));
            }
        }

        var acquisitionNodes = plan.Nodes
            .Where(node =>
                string.Equals(node.Kind, OutlineNodeKinds.Discover, StringComparison.OrdinalIgnoreCase)
                || string.Equals(node.Kind, OutlineNodeKinds.Acquire, StringComparison.OrdinalIgnoreCase))
            .Select(static node => node.Id)
            .ToList();
        if (acquisitionNodes.Count > 0 && !acquisitionNodes.Any(reachable.Contains))
            result.Issues.Add(CreateIssue("acquisition_path_missing", "No acquisition node contributes to the result."));

        return result;
    }

    private static Dictionary<string, List<string>> BuildDownstreamMap(IReadOnlyList<OutlineNode> nodes)
    {
        var result = nodes.ToDictionary(node => node.Id, static _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            foreach (var dependsOn in node.DependsOn)
            {
                if (result.TryGetValue(dependsOn, out var downstream))
                    downstream.Add(node.Id);
            }
        }

        return result;
    }

    private static HashSet<string> CollectAncestors(string nodeId, IReadOnlyList<OutlineNode> nodes)
    {
        var byId = nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(nodeId);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current))
                continue;

            if (!byId.TryGetValue(current, out var node))
                continue;

            foreach (var dependsOn in node.DependsOn)
                stack.Push(dependsOn);
        }

        return visited;
    }

    private static PlanningIssue CreateIssue(string code, string message) =>
        new()
        {
            Code = code,
            Message = message,
            Layer = "outline"
        };
}
