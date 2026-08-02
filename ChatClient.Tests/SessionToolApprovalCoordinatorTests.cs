using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Application.Services.Agentic;

namespace ChatClient.Tests;

public sealed class SessionToolApprovalCoordinatorTests
{
    [Fact]
    public async Task RequestApprovalAsync_QueuesConcurrentRequestsInFifoOrder()
    {
        var coordinator = new SessionToolApprovalCoordinator();
        var first = coordinator.RequestApprovalAsync(Request("first"));
        var second = coordinator.RequestApprovalAsync(Request("second"));

        Assert.Equal("first", coordinator.PendingRequest?.RequestId);
        Assert.True(coordinator.TryRespond("first", ToolApprovalDecision.ApproveOnce, _ => { }));
        Assert.Equal(ToolApprovalDecision.ApproveOnce, await first);
        Assert.Equal("second", coordinator.PendingRequest?.RequestId);

        Assert.True(coordinator.TryRespond("second", ToolApprovalDecision.Deny, _ => { }));
        Assert.Equal(ToolApprovalDecision.Deny, await second);
        Assert.Null(coordinator.PendingRequest);
    }

    [Fact]
    public async Task RequestApprovalAsync_CancellingHeadPublishesNextRequest()
    {
        var coordinator = new SessionToolApprovalCoordinator();
        using var cancellation = new CancellationTokenSource();
        var first = coordinator.RequestApprovalAsync(Request("first"), cancellation.Token);
        var second = coordinator.RequestApprovalAsync(Request("second"));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("second", coordinator.PendingRequest?.RequestId);
    }

    [Fact]
    public async Task TryRespond_AppliesSessionGrantBeforeUnblockingRuntime()
    {
        var coordinator = new SessionToolApprovalCoordinator();
        var policy = new SessionToolApprovalPolicy();
        var approval = coordinator.RequestApprovalAsync(Request("request"));

        Assert.True(coordinator.TryRespond(
            "request",
            ToolApprovalDecision.ApproveForSession,
            request => policy.Grant(request.ToolName, request.RuntimeAgentId)));

        Assert.Equal(ToolApprovalDecision.ApproveForSession, await approval);
        Assert.True(policy.IsApproved("ordinary_tool", "agent"));
    }

    private static SessionToolApprovalRequest Request(string requestId) => new(
        requestId,
        "ordinary_tool",
        "agent",
        "{}",
        ToolApprovalSessionScope.Tool,
        null);
}
