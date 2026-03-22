#pragma warning disable MAAI001
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace ChatClient.Api.Client.Services.Agentic;

internal sealed class NamedToolWindowCompactionStrategy(
    IReadOnlyCollection<string> registeredToolNames,
    int keepLastToolPairs) : CompactionStrategy(CompactionTriggers.Always)
{
    private readonly HashSet<string> _registeredToolNames = new(
        registeredToolNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim()),
        StringComparer.OrdinalIgnoreCase);

    public int KeepLastToolPairs { get; } = Math.Max(1, keepLastToolPairs);

    protected override ValueTask<bool> CompactCoreAsync(
        CompactionMessageIndex index,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_registeredToolNames.Count == 0)
        {
            return new ValueTask<bool>(false);
        }

        List<int> matchingGroupIndexes = [];
        for (int i = 0; i < index.Groups.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var group = index.Groups[i];
            if (group.IsExcluded || group.Kind != CompactionGroupKind.ToolCall)
            {
                continue;
            }

            if (MatchesConfiguredTool(group))
            {
                matchingGroupIndexes.Add(i);
            }
        }

        if (matchingGroupIndexes.Count <= KeepLastToolPairs)
        {
            return new ValueTask<bool>(false);
        }

        HashSet<int> preserved = matchingGroupIndexes
            .Skip(Math.Max(0, matchingGroupIndexes.Count - KeepLastToolPairs))
            .ToHashSet();

        var compactedCount = 0;
        foreach (var groupIndex in matchingGroupIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (preserved.Contains(groupIndex))
            {
                continue;
            }

            var group = index.Groups[groupIndex];
            group.IsExcluded = true;
            group.ExcludeReason = $"{nameof(NamedToolWindowCompactionStrategy)} removed older tool-call history.";
            compactedCount++;
        }

        if (compactedCount > 0)
        {
            logger.LogInformation(
                "Compacted {CompactedCount} older tool-call groups, preserving the most recent {KeepLastToolPairs}.",
                compactedCount,
                KeepLastToolPairs);
            return new ValueTask<bool>(true);
        }

        return new ValueTask<bool>(false);
    }

    private bool MatchesConfiguredTool(CompactionMessageGroup group)
    {
        foreach (var message in group.Messages)
        {
            if (message.Contents is null)
            {
                continue;
            }

            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent functionCall &&
                    _registeredToolNames.Contains(functionCall.Name))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
#pragma warning restore MAAI001
