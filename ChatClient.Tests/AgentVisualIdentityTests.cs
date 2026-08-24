using ChatClient.Api.Client.Services.Agentic;
using System.Diagnostics;

namespace ChatClient.Tests;

public sealed class AgentVisualIdentityTests
{
    [Fact]
    public void FirstTenAgentsGetDistinctBaseIcons()
    {
        var map = new AgentVisualIdentityMap();

        var visuals = Enumerable.Range(1, 10).Select(index => map.GetOrAdd($"agent-{index}")).ToArray();

        Assert.Equal(10, visuals.Select(visual => visual.Icon).Distinct().Count());
        Assert.All(visuals, visual => Assert.Equal(1, visual.Generation));
        Assert.All(visuals, visual => Assert.Equal(visual.Icon, visual.DisplayText));
    }

    [Theory]
    [InlineData(11, "🧠", 2, "🧠2")]
    [InlineData(20, "🔬", 2, "🔬2")]
    [InlineData(21, "🧠", 3, "🧠3")]
    public void IconsRepeatWithGenerationSuffix(int agentNumber, string expectedIcon, int expectedGeneration, string expectedDisplayText)
    {
        var map = new AgentVisualIdentityMap();
        AgentVisualIdentity? visual = null;

        for (var index = 1; index <= agentNumber; index++) visual = map.GetOrAdd($"agent-{index}");

        Assert.NotNull(visual);
        Assert.Equal(expectedIcon, visual.Icon);
        Assert.Equal(expectedGeneration, visual.Generation);
        Assert.Equal(expectedDisplayText, visual.DisplayText);
    }

    [Fact]
    public void RepeatedAgentKeepsSameVisualIdentity()
    {
        var map = new AgentVisualIdentityMap();

        var first = map.GetOrAdd("agent-1");
        _ = map.GetOrAdd("agent-2");
        var repeated = map.GetOrAdd("agent-1");

        Assert.Equal(first, repeated);
    }

    [Fact]
    public void ResolverPrefersStableIdOverAgentName()
    {
        var start = Span(
            new HarnessTraceAttribute("background_agents.task_id", "task-42"));
        var child = Span(
            new HarnessTraceAttribute("gen_ai.operation.name", "invoke_agent"),
            new HarnessTraceAttribute("gen_ai.agent.name", "Factory Planner"));

        var identity = AgentVisualIdentityResolver.Resolve(start, [child]);

        Assert.Equal("background_agents.task_id:task-42", identity);
    }

    [Fact]
    public void ResolverFallsBackToAgentName()
    {
        var start = Span();
        var child = Span(
            new HarnessTraceAttribute("gen_ai.operation.name", "invoke_agent"),
            new HarnessTraceAttribute("gen_ai.agent.name", "Factory Planner"));

        var identity = AgentVisualIdentityResolver.Resolve(start, [child]);

        Assert.Equal("name:Factory Planner", identity);
    }

    private static HarnessTraceSpanSnapshot Span(params HarnessTraceAttribute[] tags) => new(
        "trace",
        Guid.NewGuid().ToString("N"),
        string.Empty,
        "span",
        "test",
        ActivityKind.Internal,
        DateTimeOffset.UtcNow,
        TimeSpan.Zero,
        ActivityStatusCode.Ok,
        null,
        tags,
        [],
        false,
        []);
}
