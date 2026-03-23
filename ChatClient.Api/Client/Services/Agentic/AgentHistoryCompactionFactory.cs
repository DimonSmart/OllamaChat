#pragma warning disable MAAI001
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;

namespace ChatClient.Api.Client.Services.Agentic;

internal sealed record AgentHistoryCompactionAttachment(
    AIContextProvider Provider,
    string InstructionNote,
    IReadOnlyCollection<string> RegisteredToolNames);

internal static class AgentHistoryCompactionFactory
{
    private const string StateKeyPrefix = "agentic:history-compaction";

    public static AgentHistoryCompactionAttachment? Create(
        AgentDefinition agent,
        AgenticToolSet toolSet,
        ILoggerFactory loggerFactory,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(toolSet);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logger);

        var settings = agent.ExecutionSettings.HistoryCompaction;
        if (!settings.Enabled)
        {
            return null;
        }

        if (!string.Equals(settings.Mode, AgentHistoryCompactionModes.ToolWindow, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "History compaction mode {Mode} is not supported for agent {AgentName}.",
                settings.Mode,
                agent.AgentName);
            return null;
        }

        if (settings.KeepLastToolPairs <= 0)
        {
            logger.LogWarning(
                "History compaction is enabled for agent {AgentName} but KeepLastToolPairs is not positive.",
                agent.AgentName);
            return null;
        }

        var registeredToolNames = ResolveRegisteredToolNames(settings.ToolNames, toolSet.MetadataByName);
        if (registeredToolNames.Count == 0)
        {
            logger.LogWarning(
                "History compaction is enabled for agent {AgentName} but no matching tools were resolved.",
                agent.AgentName);
            return null;
        }

        var strategy = new NamedToolWindowCompactionStrategy(
            registeredToolNames,
            settings.KeepLastToolPairs);

        var provider = new CompactionProvider(
            strategy,
            BuildStateKey(agent),
            loggerFactory);

        return new AgentHistoryCompactionAttachment(
            Provider: provider,
            InstructionNote: BuildInstructionNote(registeredToolNames, settings.KeepLastToolPairs),
            RegisteredToolNames: registeredToolNames);
    }

    public static AgentHistoryCompactionAttachment? Create(
        AgentDescription agent,
        AgenticToolSet toolSet,
        ILoggerFactory loggerFactory,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return Create(
            AgentDefinitionMapper.ToDefinition(agent),
            toolSet,
            loggerFactory,
            logger);
    }

    internal static IReadOnlyCollection<string> ResolveRegisteredToolNames(
        IReadOnlyCollection<string> configuredToolNames,
        IReadOnlyDictionary<string, AgenticRegisteredTool> metadataByName)
    {
        ArgumentNullException.ThrowIfNull(configuredToolNames);
        ArgumentNullException.ThrowIfNull(metadataByName);

        HashSet<string> resolved = new(StringComparer.OrdinalIgnoreCase);
        if (configuredToolNames.Count == 0 || metadataByName.Count == 0)
        {
            return resolved.ToArray();
        }

        foreach (var configuredName in configuredToolNames)
        {
            if (string.IsNullOrWhiteSpace(configuredName))
            {
                continue;
            }

            var normalized = configuredName.Trim();
            if (metadataByName.ContainsKey(normalized))
            {
                resolved.Add(normalized);
            }

            foreach (var pair in metadataByName)
            {
                if (string.Equals(pair.Value.ToolName, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    resolved.Add(pair.Key);
                }
            }
        }

        return resolved.ToArray();
    }

    private static string BuildInstructionNote(
        IReadOnlyCollection<string> registeredToolNames,
        int keepLastToolPairs)
    {
        var toolList = string.Join(", ", registeredToolNames);
        return $"Some earlier chat history for tool calls ({toolList}) may be compacted during execution. Only the most recent {keepLastToolPairs} matching tool result pair(s) remain visible.";
    }

    private static string BuildStateKey(AgentDefinition agent)
    {
        var agentKey = agent.Id != Guid.Empty
            ? agent.Id.ToString("N")
            : (string.IsNullOrWhiteSpace(agent.ShortName) ? agent.AgentName : agent.ShortName) ?? "agent";

        return $"{StateKeyPrefix}:{agentKey}";
    }
}
#pragma warning restore MAAI001
