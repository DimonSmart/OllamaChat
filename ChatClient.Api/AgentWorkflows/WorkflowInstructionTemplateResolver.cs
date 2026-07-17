using ChatClient.Domain.Models;
using System.Text.RegularExpressions;

namespace ChatClient.Api.AgentWorkflows;

internal static class WorkflowInstructionTemplateResolver
{
    private static readonly Regex AgentPlaceholderRegex = new(
        @"\{\{agent:(?<agentId>[A-Za-z0-9_-]+)\.(?<property>[A-Za-z][A-Za-z0-9]*)\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> SupportedProperties =
    [
        "displayName",
        "role",
        "id",
        "avatarText"
    ];

    public static void ValidateAgentReferences(IEnumerable<WorkflowParticipantDefinition> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);

        var agentsById = agents.ToDictionary(agent => agent.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var agent in agents)
        {
            ValidateText(agent.Id, "agent draft instructions", ResolveInlineAgent(agent)?.Content, agentsById);
            ValidateText(agent.Id, "override instructions", agent.Overrides.Llm?.Instructions, agentsById);
            ValidateText(agent.Id, "appended instructions", agent.Overrides.Llm?.AppendedInstructions, agentsById);
            ValidateText(agent.Id, "legacy override instructions", agent.DraftOverrides.Instructions, agentsById);
            ValidateText(agent.Id, "legacy appended instructions", agent.DraftOverrides.AppendedInstructions, agentsById);
        }
    }

    public static string ResolveAgentReferences(
        string content,
        string ownerAgentId,
        IReadOnlyDictionary<string, WorkflowParticipantDefinition> agentsById)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(agentsById);

        ValidateText(ownerAgentId, "materialized instructions", content, agentsById);

        if (!content.Contains("{{agent:", StringComparison.Ordinal))
        {
            return content;
        }

        return AgentPlaceholderRegex.Replace(content, match =>
        {
            var referencedAgentId = match.Groups["agentId"].Value;
            var property = match.Groups["property"].Value;
            var referencedAgent = agentsById[referencedAgentId];

            return property.ToLowerInvariant() switch
            {
                "displayname" => ResolveDisplayName(referencedAgent),
                "role" => referencedAgent.Role,
                "id" => referencedAgent.Id,
                "avatartext" => ResolveInlineAgent(referencedAgent)?.AvatarText?.Trim() ?? string.Empty,
                _ => throw new InvalidOperationException(
                    $"Workflow agent '{ownerAgentId}' references unsupported property '{property}' in placeholder '{match.Value}'.")
            };
        });
    }

    private static void ValidateText(
        string ownerAgentId,
        string fieldName,
        string? content,
        IReadOnlyDictionary<string, WorkflowParticipantDefinition> agentsById)
    {
        if (string.IsNullOrWhiteSpace(content) ||
            !content.Contains("{{agent:", StringComparison.Ordinal))
        {
            return;
        }

        var searchStart = 0;
        while (true)
        {
            var placeholderStart = content.IndexOf("{{agent:", searchStart, StringComparison.Ordinal);
            if (placeholderStart < 0)
            {
                return;
            }

            var placeholderEnd = content.IndexOf("}}", placeholderStart, StringComparison.Ordinal);
            if (placeholderEnd < 0)
            {
                throw new InvalidOperationException(
                    $"Workflow agent '{ownerAgentId}' has malformed {fieldName}. Placeholder starting at '{content[placeholderStart..]}' is missing '}}'.");
            }

            var rawPlaceholder = content[placeholderStart..(placeholderEnd + 2)];
            var match = AgentPlaceholderRegex.Match(rawPlaceholder);
            if (!match.Success || match.Length != rawPlaceholder.Length)
            {
                throw new InvalidOperationException(
                    $"Workflow agent '{ownerAgentId}' has invalid {fieldName} placeholder '{rawPlaceholder}'. Expected '{{{{agent:<agentId>.<property>}}}}'.");
            }

            var referencedAgentId = match.Groups["agentId"].Value;
            var property = match.Groups["property"].Value;

            if (!agentsById.ContainsKey(referencedAgentId))
            {
                throw new InvalidOperationException(
                    $"Workflow agent '{ownerAgentId}' references undefined agent '{referencedAgentId}' in {fieldName} placeholder '{rawPlaceholder}'.");
            }

            if (!SupportedProperties.Contains(property))
            {
                throw new InvalidOperationException(
                    $"Workflow agent '{ownerAgentId}' references unsupported property '{property}' in {fieldName} placeholder '{rawPlaceholder}'. Supported properties: {string.Join(", ", SupportedProperties)}.");
            }

            searchStart = placeholderEnd + 2;
        }
    }

    private static string ResolveDisplayName(WorkflowParticipantDefinition agent)
    {
        var displayName = ResolveInlineAgent(agent)?.AgentName?.Trim();
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        return !string.IsNullOrWhiteSpace(agent.Role)
            ? agent.Role
            : agent.Id;
    }

    private static AgentTemplateDefinition? ResolveInlineAgent(WorkflowParticipantDefinition agent) =>
        agent.Source is InlineAgentParticipantSource inline
            ? inline.Agent
            : agent.AgentDraft;
}
