namespace ChatClient.Api.Client.Services.Agentic;

internal sealed record AgentVisualIdentity(string Icon, int Generation)
{
    public string DisplayText => Generation == 1 ? Icon : $"{Icon}{Generation}";
}

internal sealed class AgentVisualIdentityMap
{
    private static readonly string[] Icons = ["🧠", "🔧", "🔎", "🚀", "💡", "⚙️", "🧩", "📐", "🛠️", "🔬"];
    private readonly Dictionary<string, AgentVisualIdentity> _visuals = new(StringComparer.Ordinal);

    public AgentVisualIdentity GetOrAdd(string agentIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentIdentity);
        if (_visuals.TryGetValue(agentIdentity, out var existing))
            return existing;

        var ordinal = _visuals.Count;
        var visual = new AgentVisualIdentity(Icons[ordinal % Icons.Length], ordinal / Icons.Length + 1);
        _visuals.Add(agentIdentity, visual);
        return visual;
    }
}

internal static class AgentVisualIdentityResolver
{
    private static readonly string[] IdentityTagKeys =
    [
        "gen_ai.agent.id",
        "background_agents.agent_id",
        "agent.id",
        "agent_id",
        "background_agents.task_id",
        "task.id",
        "task_id"
    ];

    public static string? Resolve(HarnessTraceSpanSnapshot startSpan, IEnumerable<HarnessTraceSpanSnapshot> childSpans)
    {
        ArgumentNullException.ThrowIfNull(startSpan);
        ArgumentNullException.ThrowIfNull(childSpans);

        var agentSpans = childSpans.Where(IsAgentInvocation).ToArray();
        foreach (var span in new[] { startSpan }.Concat(agentSpans))
        {
            foreach (var key in IdentityTagKeys)
            {
                var value = TagValue(span, key);
                if (!string.IsNullOrWhiteSpace(value))
                    return $"{key}:{value}";
            }
        }

        var agentName = FindAgentName(startSpan, agentSpans);
        return string.IsNullOrWhiteSpace(agentName) ? null : $"name:{agentName}";
    }

    public static string? FindAgentName(HarnessTraceSpanSnapshot startSpan, IEnumerable<HarnessTraceSpanSnapshot> childSpans)
    {
        ArgumentNullException.ThrowIfNull(startSpan);
        ArgumentNullException.ThrowIfNull(childSpans);

        var name = TagValue(startSpan, "gen_ai.agent.name");
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        return childSpans
            .Where(IsAgentInvocation)
            .Select(span => TagValue(span, "gen_ai.agent.name"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static bool IsAgentInvocation(HarnessTraceSpanSnapshot span) =>
        string.Equals(TagValue(span, "gen_ai.operation.name"), "invoke_agent", StringComparison.Ordinal);

    private static string? TagValue(HarnessTraceSpanSnapshot span, string key) =>
        span.Tags.FirstOrDefault(tag => tag.Key == key)?.Value;
}
