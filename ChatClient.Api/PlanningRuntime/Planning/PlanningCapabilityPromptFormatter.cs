using System.Text;
using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.Services;

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
            sb.AppendLine($"- name: {tool.QualifiedName}");
            sb.AppendLine($"  description: {ResolvePrimaryDescription(tool)}");

            if (!string.IsNullOrWhiteSpace(tool.PlanningMetadata?.UseWhen))
                sb.AppendLine($"  useWhen: {tool.PlanningMetadata.UseWhen}");
            if (!string.IsNullOrWhiteSpace(tool.PlanningMetadata?.AvoidWhen))
                sb.AppendLine($"  avoidWhen: {tool.PlanningMetadata.AvoidWhen}");
            if (!string.IsNullOrWhiteSpace(tool.PlanningMetadata?.Returns))
                sb.AppendLine($"  returns: {tool.PlanningMetadata.Returns}");
            if (!string.IsNullOrWhiteSpace(tool.PlanningMetadata?.Constraints))
                sb.AppendLine($"  constraints: {tool.PlanningMetadata.Constraints}");

            sb.AppendLine($"  inputSchema: {PlanningJson.SerializeElementCompact(tool.InputSchema)}");
            sb.AppendLine($"  outputSchema: {PlanningJson.SerializeElementCompact(tool.OutputSchema)}");
        }
    }

    private static string ResolvePrimaryDescription(AppToolDescriptor tool)
    {
        if (!string.IsNullOrWhiteSpace(tool.PlanningMetadata?.Purpose))
            return tool.PlanningMetadata.Purpose;

        return tool.Description;
    }
}
