using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.Services;
using System.Text;

namespace ChatClient.Api.PlanningRuntime.Planning;

internal static class PlanningCapabilityPromptFormatter
{
    public static void AppendAgents(StringBuilder sb, IReadOnlyCollection<PlanningCallableAgentDescriptor> agents)
    {
        sb.AppendLine("Available saved agents:");
        if (agents.Count == 0)
        {
            sb.AppendLine("- none");
            return;
        }

        foreach (var agent in agents)
        {
            sb.AppendLine($"- id: {agent.Name}");
            sb.AppendLine($"  name: {agent.DisplayName}");
            sb.AppendLine($"  description: {agent.Description}");
        }
    }

    public static void AppendTools(StringBuilder sb, IReadOnlyCollection<AppToolDescriptor> tools)
    {
        sb.AppendLine("Available tools:");
        foreach (var tool in tools)
        {
            sb.AppendLine($"- toolId: {tool.QualifiedName}");
            sb.AppendLine($"  description: {ResolvePrimaryDescription(tool)}");
            if (tool.PlanningMetadata?.PlannerRole is { } plannerRole)
                sb.AppendLine($"  role: {ToPromptValue(plannerRole)}");
            if (tool.PlanningMetadata?.ProducesKind is { } producesKind)
                sb.AppendLine($"  produces: {ToPromptValue(producesKind)}");
            sb.AppendLine($"  readOnly: {tool.ReadOnlyHint.ToString().ToLowerInvariant()}");
            sb.AppendLine($"  destructive: {tool.DestructiveHint.ToString().ToLowerInvariant()}");
            sb.AppendLine($"  openWorld: {tool.OpenWorldHint.ToString().ToLowerInvariant()}");
            if (tool.MayRequireUserInput)
                sb.AppendLine("  mayRequireUserInput: true");

            if (!string.IsNullOrWhiteSpace(tool.PlanningMetadata?.UseWhen))
                sb.AppendLine($"  useWhen: {tool.PlanningMetadata.UseWhen}");
            if (!string.IsNullOrWhiteSpace(tool.PlanningMetadata?.AvoidWhen))
                sb.AppendLine($"  avoidWhen: {tool.PlanningMetadata.AvoidWhen}");
            if (!string.IsNullOrWhiteSpace(tool.PlanningMetadata?.Returns))
                sb.AppendLine($"  returns: {tool.PlanningMetadata.Returns}");
            if (!string.IsNullOrWhiteSpace(tool.PlanningMetadata?.Limits))
                sb.AppendLine($"  limits: {tool.PlanningMetadata.Limits}");
            if (!string.IsNullOrWhiteSpace(tool.PlanningMetadata?.Constraints))
                sb.AppendLine($"  constraints: {tool.PlanningMetadata.Constraints}");
        }
    }

    private static string ResolvePrimaryDescription(AppToolDescriptor tool)
    {
        if (!string.IsNullOrWhiteSpace(tool.PlanningMetadata?.Purpose))
            return tool.PlanningMetadata.Purpose;

        return tool.Description;
    }

    private static string ToPromptValue(AppToolPlannerRole role) =>
        role switch
        {
            AppToolPlannerRole.Discover => "discover",
            AppToolPlannerRole.Acquire => "acquire",
            AppToolPlannerRole.Transform => "transform",
            AppToolPlannerRole.Act => "act",
            _ => role.ToString().ToLowerInvariant()
        };

    private static string ToPromptValue(AppToolProducesKind producesKind) =>
        producesKind switch
        {
            AppToolProducesKind.Reference => "reference",
            AppToolProducesKind.Document => "document",
            AppToolProducesKind.StructuredData => "structured_data",
            AppToolProducesKind.SideEffect => "side_effect",
            _ => producesKind.ToString().ToLowerInvariant()
        };
}
