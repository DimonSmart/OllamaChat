using ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Tools;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Outline;

public sealed class OutlineRepairer
{
    public Contracts.OutlinePlan Repair(Contracts.OutlinePlan plan, IReadOnlyList<ExperimentIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(issues);

        var tools = new OutlinePlanEditingTools(plan);
        foreach (var issue in issues)
        {
            switch (issue.Code)
            {
                case "result_node_missing":
                case "result_node_non_terminal":
                    MarkSingleTerminalNodeAsResult(tools);
                    break;
                case "non_result_node_unused":
                case "disconnected_subgraph":
                    RemoveUnusedTerminalNodes(tools);
                    break;
            }
        }

        return tools.ReadPlan();
    }

    private static void MarkSingleTerminalNodeAsResult(OutlinePlanEditingTools tools)
    {
        var plan = tools.ReadPlan();
        var terminals = plan.Nodes
            .Where(node => !plan.Nodes.Any(candidate => candidate.DependsOn.Contains(node.Id, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        if (terminals.Count == 1)
            tools.MarkResultNode(terminals[0].Id);
    }

    private static void RemoveUnusedTerminalNodes(OutlinePlanEditingTools tools)
    {
        var plan = tools.ReadPlan();
        foreach (var node in plan.Nodes.ToList())
        {
            if (string.Equals(node.Id, plan.ResultNodeId, StringComparison.OrdinalIgnoreCase))
                continue;

            var hasDownstream = plan.Nodes.Any(candidate => candidate.DependsOn.Contains(node.Id, StringComparer.OrdinalIgnoreCase));
            if (!hasDownstream)
                tools.RemoveNode(node.Id);
        }
    }
}
