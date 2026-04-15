namespace ChatClient.Api.PlanningRuntime.Outline;

public sealed class OutlineRepairer
{
    public OutlinePlan Repair(OutlinePlan plan, IReadOnlyList<Shared.PlanningIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(issues);

        var workingPlan = Clone(plan);
        foreach (var issue in issues)
        {
            switch (issue.Code)
            {
                case "result_node_missing":
                case "result_node_non_terminal":
                    workingPlan = MarkSingleTerminalNodeAsResult(workingPlan);
                    break;
                case "non_result_node_unused":
                case "disconnected_subgraph":
                    workingPlan = RemoveUnusedTerminalNodes(workingPlan);
                    break;
            }
        }

        return workingPlan;
    }

    private static OutlinePlan MarkSingleTerminalNodeAsResult(OutlinePlan plan)
    {
        var terminals = plan.Nodes
            .Where(node => !plan.Nodes.Any(candidate => candidate.DependsOn.Contains(node.Id, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        if (terminals.Count != 1)
            return plan;

        return new OutlinePlan
        {
            Goal = plan.Goal,
            BlockedReason = plan.BlockedReason,
            ResultNodeId = terminals[0].Id,
            RequiredDeliverables = [.. plan.RequiredDeliverables],
            Nodes = [.. plan.Nodes]
        };
    }

    private static OutlinePlan RemoveUnusedTerminalNodes(OutlinePlan plan)
    {
        var filteredNodes = plan.Nodes
            .Where(node =>
                string.Equals(node.Id, plan.ResultNodeId, StringComparison.OrdinalIgnoreCase)
                || plan.Nodes.Any(candidate => candidate.DependsOn.Contains(node.Id, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        return new OutlinePlan
        {
            Goal = plan.Goal,
            BlockedReason = plan.BlockedReason,
            ResultNodeId = plan.ResultNodeId,
            RequiredDeliverables = [.. plan.RequiredDeliverables],
            Nodes = filteredNodes
        };
    }

    private static OutlinePlan Clone(OutlinePlan plan) =>
        new()
        {
            Goal = plan.Goal,
            BlockedReason = plan.BlockedReason,
            ResultNodeId = plan.ResultNodeId,
            RequiredDeliverables = [.. plan.RequiredDeliverables],
            Nodes = [.. plan.Nodes.Select(static node => new OutlineNode
            {
                Id = node.Id,
                Kind = node.Kind,
                Purpose = node.Purpose,
                DependsOn = [.. node.DependsOn],
                Inputs = [.. node.Inputs.Select(static input => new OutlineNodeInput
                {
                    Name = input.Name,
                    SemanticType = input.SemanticType,
                    FromNodeId = input.FromNodeId
                })],
                Outputs = [.. node.Outputs.Select(static output => new OutlineNodeOutput
                {
                    Name = output.Name,
                    SemanticType = output.SemanticType
                })],
                Constraints = [.. node.Constraints],
                Notes = [.. node.Notes]
            })]
        };
}
