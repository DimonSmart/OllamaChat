using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Application.Services.Agentic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatClient.Tests;

public sealed class HarnessResponseEventProjectorTests
{
    [Fact]
    public void Project_MapsTextCallAndResultByCallId()
    {
        var projector = new HarnessResponseEventProjector(
            NullLogger<HarnessResponseEventProjector>.Instance).CreateProjection();
        var metadata = new Dictionary<string, AgenticRegisteredTool>(StringComparer.OrdinalIgnoreCase)
        {
            ["registered_search"] = new(
                "registered_search", "Docs", "search", "mcp", "Project docs", true, null!)
        };
        var startedAt = DateTimeOffset.UtcNow;

        var startEvents = projector.Project(new AgentResponseUpdate(ChatRole.Assistant,
        [
            new TextContent("Searching"),
            new FunctionCallContent("call-1", "registered_search", new Dictionary<string, object?>
            {
                ["query"] = "Harness"
            })
        ])
        { CreatedAt = startedAt }, metadata);
        var resultEvents = projector.Project(new AgentResponseUpdate(ChatRole.Assistant,
        [
            new FunctionResultContent("call-1", new { hits = 2 })
        ]), metadata);

        Assert.Equal("Searching", Assert.IsType<HarnessTextDelta>(startEvents[0]).Text);
        var started = Assert.IsType<HarnessToolCallStarted>(startEvents[1]);
        Assert.Equal("call-1", started.CallId);
        Assert.Equal("search", started.OriginalName);
        Assert.Equal("Docs", started.ServerName);
        Assert.Equal("Project docs", started.BindingName);
        Assert.Contains("Harness", started.Arguments);

        var completed = Assert.IsType<HarnessToolCallCompleted>(Assert.Single(resultEvents));
        Assert.Equal(started.CallId, completed.CallId);
        Assert.Equal(started.StartedAt, completed.StartedAt);
        Assert.Contains("2", completed.Result);
    }

    [Fact]
    public void Project_MapsFailureAndIgnoresUnknownContent()
    {
        var projector = new HarnessResponseEventProjector(
            NullLogger<HarnessResponseEventProjector>.Instance).CreateProjection();
        var metadata = new Dictionary<string, AgenticRegisteredTool>();
        projector.Project(new AgentResponseUpdate(ChatRole.Assistant,
        [
            new FunctionCallContent("call-2", "broken")
        ]), metadata);
        var failedResult = new FunctionResultContent("call-2", null)
        {
            Exception = new InvalidOperationException("boom")
        };

        var events = projector.Project(new AgentResponseUpdate(ChatRole.Assistant,
        [
            new UnknownContent(),
            failedResult
        ]), metadata);

        var failed = Assert.IsType<HarnessToolCallFailed>(Assert.Single(events));
        Assert.Equal("call-2", failed.CallId);
        Assert.Equal("boom", failed.Error);
    }

    private sealed class UnknownContent : AIContent;
}
