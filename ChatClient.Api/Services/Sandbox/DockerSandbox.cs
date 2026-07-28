using ChatClient.Application.Services.Sandbox;
using Microsoft.Agents.AI.Tools.Shell;

namespace ChatClient.Api.Services.Sandbox;

public sealed class DockerSandbox : ISandbox
{
    private readonly DockerShellExecutor _executor;
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    public DockerSandbox(
        string workspacePath,
        DockerShellExecutor executor)
    {
        WorkspacePath = workspacePath;
        _executor = executor;
    }

    public string ProviderType => DockerSandboxProvider.ProviderType;

    public string WorkspacePath { get; }

    public SandboxState State { get; private set; } = SandboxState.Created;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (State is SandboxState.Running)
        {
            return;
        }

        State = SandboxState.Starting;
        try
        {
            await _executor.InitializeAsync(cancellationToken);
            State = SandboxState.Running;
        }
        catch
        {
            State = SandboxState.Failed;
            throw;
        }
    }

    public async Task<SandboxCommandResult> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            var result = await _executor.RunAsync(command, cancellationToken);
            State = SandboxState.Running;
            return new SandboxCommandResult(
                result.Stdout,
                result.Stderr,
                result.ExitCode,
                result.TimedOut);
        }
        catch
        {
            State = SandboxState.Failed;
            throw;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        State = SandboxState.Stopping;
        try
        {
            await _executor.DisposeAsync();
            State = SandboxState.Stopped;
        }
        catch
        {
            State = SandboxState.Failed;
            throw;
        }
        finally
        {
            _commandGate.Dispose();
        }
    }
}
