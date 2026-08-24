using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.Sandbox;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Text.Json;

namespace ChatClient.Api.Services.Sandbox;

public sealed class SessionSandboxShellExecutor : ShellExecutor
{
    private readonly ISandbox _sandbox;

    public SessionSandboxShellExecutor(ISandbox sandbox)
    {
        _sandbox = sandbox;
    }

    public override async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        await _sandbox.InitializeAsync(cancellationToken);

    public override async Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var result = await _sandbox.ExecuteAsync(command, cancellationToken);
        return new ShellResult(
            result.StandardOutput,
            result.StandardError,
            result.ExitCode,
            DateTime.UtcNow - startedAt,
            TimedOut: result.TimedOut);
    }

    public override AIFunction AsAIFunction(string name = SandboxToolNames.RunShell, string? description = null, bool requireApproval = true)
    {
        description ??= "Execute a single shell command inside the session sandbox and return stdout, stderr, and exit code. The sandbox starts in /workspace and persists for the life of the current chat session.";
        var fn = AIFunctionFactory.Create(
            async ([Description("The shell command to execute.")] string command, CancellationToken cancellationToken) =>
            {
                var result = await RunAsync(command, cancellationToken);
                return result.FormatForModel();
            },
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description
            });
        return requireApproval ? new ApprovalRequiredAIFunction(fn) : fn;
    }

    public AIFunction AsCoordinatedAIFunction(
        ISessionToolApprovalCoordinator approvalCoordinator,
        SessionToolApprovalPolicy approvalPolicy,
        string runtimeAgentId,
        string workspacePath,
        string name = SandboxToolNames.RunShell,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(approvalCoordinator);
        ArgumentNullException.ThrowIfNull(approvalPolicy);
        description ??= "Execute a single shell command inside the session sandbox and return stdout, stderr, and exit code. The sandbox starts in /workspace and persists for the life of the current chat session.";

        return AIFunctionFactory.Create(
            async ([Description("The shell command to execute.")] string command, CancellationToken cancellationToken) =>
            {
                if (!approvalPolicy.IsApproved(name, runtimeAgentId))
                {
                    var decision = await approvalCoordinator.RequestApprovalAsync(
                        new SessionToolApprovalRequest(
                            $"sandbox-shell-{Guid.NewGuid():N}",
                            name,
                            runtimeAgentId,
                            JsonSerializer.Serialize(new { command }),
                            ToolApprovalSessionScope.SandboxCommands,
                            workspacePath),
                        cancellationToken);

                    if (decision == ToolApprovalDecision.Deny)
                    {
                        return "Sandbox command was denied by the user.";
                    }

                    if (decision == ToolApprovalDecision.ApproveForSession)
                    {
                        approvalPolicy.Grant(name, runtimeAgentId);
                    }
                }

                var result = await RunAsync(command, cancellationToken);
                return result.FormatForModel();
            },
            new AIFunctionFactoryOptions
            {
                Name = name,
                Description = description
            });
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
