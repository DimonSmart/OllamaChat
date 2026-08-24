using ChatClient.Api.Services.Sandbox;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.Sandbox;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;

namespace ChatClient.Tests;

public sealed class SessionSandboxShellExecutorTests
{
    [Fact]
    public async Task CoordinatedFunction_SessionApprovalCoversLaterBackgroundCommands()
    {
        var sandbox = new RecordingSandbox();
        var coordinator = new RecordingApprovalCoordinator(ToolApprovalDecision.ApproveForSession);
        var policy = new SessionToolApprovalPolicy();
        policy.SetWorkspace(sandbox.WorkspacePath);
        var function = new SessionSandboxShellExecutor(sandbox).AsCoordinatedAIFunction(
            coordinator,
            policy,
            "worker",
            sandbox.WorkspacePath);

        await InvokeAsync(function, "dotnet --info");
        await InvokeAsync(function, "dotnet test");

        var request = Assert.Single(coordinator.Requests);
        Assert.Equal(ToolApprovalSessionScope.SandboxCommands, request.SessionScope);
        Assert.Equal(sandbox.WorkspacePath, request.WorkspacePath);
        Assert.Contains("dotnet --info", request.Arguments);
        Assert.Equal(["dotnet --info", "dotnet test"], sandbox.Commands);
    }

    [Fact]
    public async Task CoordinatedFunction_DenialDoesNotExecuteCommand()
    {
        var sandbox = new RecordingSandbox();
        var coordinator = new RecordingApprovalCoordinator(ToolApprovalDecision.Deny);
        var function = new SessionSandboxShellExecutor(sandbox).AsCoordinatedAIFunction(
            coordinator,
            new SessionToolApprovalPolicy(),
            "worker",
            sandbox.WorkspacePath);

        var result = await InvokeAsync(function, "dotnet build");

        Assert.Contains("denied", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(sandbox.Commands);
    }

    private static async Task<string> InvokeAsync(AIFunction function, string command)
    {
        var arguments = new AIFunctionArguments
        {
            ["command"] = command
        };
        var result = await function.InvokeAsync(
            arguments,
            cancellationToken: TestContext.Current.CancellationToken);
        return result?.ToString() ?? string.Empty;
    }

    private sealed class RecordingApprovalCoordinator(ToolApprovalDecision decision)
        : ISessionToolApprovalCoordinator
    {
        public List<SessionToolApprovalRequest> Requests { get; } = [];

        public SessionToolApprovalRequest? PendingRequest => null;

        public event Action? PendingRequestChanged;

        public Task<ToolApprovalDecision> RequestApprovalAsync(
            SessionToolApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(decision);
        }

        public bool TryRespond(
            string requestId,
            ToolApprovalDecision response,
            Action<SessionToolApprovalRequest> beforeCompletion) => false;

        public void CancelPending() => PendingRequestChanged?.Invoke();
    }

    private sealed class RecordingSandbox : ISandbox
    {
        public string ProviderType => "test";

        public string WorkspacePath => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sandbox-workspace"));

        public SandboxState State => SandboxState.Running;

        public List<string> Commands { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SandboxCommandResult> ExecuteAsync(
            string command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(new SandboxCommandResult("ok", string.Empty, 0, false));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
