namespace ChatClient.Api.AgentWorkflows;

public static class GroupChatManagerPrograms
{
    public static GroupChatManagerProgram RoundRobin()
    {
        return new GroupChatManagerProgram(
            static ctx =>
            {
                if (ctx.ParticipantAgentIds.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Round robin manager requires at least one participant.");
                }

                return ctx.ParticipantAgentIds[ctx.AssistantMessageIndex % ctx.ParticipantAgentIds.Count];
            },
            displayName: "RoundRobin");
    }

    public static GroupChatManagerProgram PrefixCycleSuffix(
        IEnumerable<string> prefix,
        IEnumerable<string> cycle,
        IEnumerable<string> suffix)
    {
        var normalizedPrefix = NormalizeAgentIds(prefix, nameof(prefix));
        var normalizedCycle = NormalizeAgentIds(cycle, nameof(cycle));
        var normalizedSuffix = NormalizeAgentIds(suffix, nameof(suffix));

        if (normalizedCycle.Count == 0)
        {
            throw new ArgumentException(
                "Cycle must contain at least one agent id.",
                nameof(cycle));
        }

        return CreatePrefixCycleSuffixProgram(
            normalizedPrefix,
            normalizedCycle,
            normalizedSuffix,
            "PrefixCycleSuffix");
    }

    public static GroupChatManagerProgram OpenCycleClose(
        IEnumerable<string> opening,
        IEnumerable<string> cycle,
        IEnumerable<string> closing)
    {
        var normalizedOpening = NormalizeAgentIds(opening, nameof(opening));
        var normalizedCycle = NormalizeAgentIds(cycle, nameof(cycle));
        var normalizedClosing = NormalizeAgentIds(closing, nameof(closing));

        if (normalizedCycle.Count == 0)
        {
            throw new ArgumentException(
                "Cycle must contain at least one agent id.",
                nameof(cycle));
        }

        return CreatePrefixCycleSuffixProgram(
            normalizedOpening,
            normalizedCycle,
            normalizedClosing,
            "OpenCycleClose");
    }

    private static GroupChatManagerProgram CreatePrefixCycleSuffixProgram(
        IReadOnlyList<string> prefix,
        IReadOnlyList<string> cycle,
        IReadOnlyList<string> suffix,
        string displayName)
    {
        return new GroupChatManagerProgram(
            ctx =>
            {
                if (ctx.AssistantMessageIndex < prefix.Count)
                {
                    return prefix[ctx.AssistantMessageIndex];
                }

                var suffixStartIndex = Math.Max(prefix.Count, ctx.MaximumIterations - suffix.Count);
                if (ctx.AssistantMessageIndex >= suffixStartIndex)
                {
                    var suffixIndex = ctx.AssistantMessageIndex - suffixStartIndex;
                    if (suffixIndex >= 0 && suffixIndex < suffix.Count)
                    {
                        return suffix[suffixIndex];
                    }
                }

                var cycleIndex = ctx.AssistantMessageIndex - prefix.Count;
                return cycle[cycleIndex % cycle.Count];
            },
            displayName);
    }

    private static IReadOnlyList<string> NormalizeAgentIds(
        IEnumerable<string> agentIds,
        string paramName)
    {
        ArgumentNullException.ThrowIfNull(agentIds);

        return agentIds
            .Select(agentId =>
            {
                if (string.IsNullOrWhiteSpace(agentId))
                {
                    throw new ArgumentException("Agent id is required.", paramName);
                }

                return agentId.Trim();
            })
            .ToArray();
    }
}
