using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Sandbox;

public interface ISandboxDefinition
{
}

public interface ISandboxProvider
{
    string Type { get; }

    string DisplayName { get; }

    string DefaultConfiguration { get; }

    ISandboxDefinition ParseDefinition(string configuration);

    SandboxDefinitionValidation ValidateDefinition(ISandboxDefinition definition);

    SandboxDefinitionSummary GetSummary(ISandboxDefinition definition);

    Task<SandboxProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default);

    Task<ISandbox> CreateAsync(
        ISandboxDefinition definition,
        SandboxCreateContext context,
        CancellationToken cancellationToken = default);
}

public interface ISandboxProviderRegistry
{
    IReadOnlyList<SandboxProviderDescriptor> GetProviders();

    ISandboxProvider GetRequired(string providerType);

    bool TryGet(string providerType, out ISandboxProvider provider);
}

public interface ISandboxSessionFactory
{
    Task<SandboxSessionHandle> StartAsync(
        Guid profileId,
        string workspacePath,
        string sessionId,
        CancellationToken cancellationToken = default,
        IProgress<ChatSessionStartProgress>? progress = null);
}

public interface ISandbox : IAsyncDisposable
{
    string ProviderType { get; }

    string WorkspacePath { get; }

    SandboxState State { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<SandboxCommandResult> ExecuteAsync(string command, CancellationToken cancellationToken = default);
}

public sealed record SandboxProviderDescriptor(
    string Type,
    string DisplayName,
    string DefaultConfiguration);

public sealed record SandboxDefinitionValidation(
    bool IsValid,
    IReadOnlyList<string> Errors)
{
    public static SandboxDefinitionValidation Success { get; } = new(true, []);

    public static SandboxDefinitionValidation Failure(params IReadOnlyList<string> errors) =>
        new(false, errors);
}

public sealed record SandboxDefinitionSummary(
    string Image,
    string Limits,
    string Network,
    bool HasRootWarning = false,
    string? RootWarning = null);

public sealed record SandboxProviderAvailability(
    bool IsAvailable,
    string? ErrorMessage = null);

public enum SandboxState
{
    Created,
    Starting,
    Running,
    Stopping,
    Stopped,
    Failed
}

public sealed record SandboxCommandResult(
    string StandardOutput,
    string StandardError,
    int ExitCode,
    bool TimedOut);

public sealed record SandboxCreateContext
{
    public required Guid ProfileId { get; init; }

    public required string SessionId { get; init; }

    public required string WorkspacePath { get; init; }

    public required string ProfileName { get; init; }
}

public sealed class SandboxSessionHandle : IAsyncDisposable
{
    private readonly Func<ValueTask> _disposeAsync;

    public SandboxSessionHandle(Func<ValueTask> disposeAsync)
    {
        _disposeAsync = disposeAsync;
    }

    public required Guid ProfileId { get; init; }

    public required string ProfileName { get; init; }

    public required string ProviderType { get; init; }

    public required SandboxDefinitionSummary Summary { get; init; }

    public required string WorkspacePath { get; init; }

    public required ISandbox Instance { get; init; }

    public ValueTask DisposeAsync() => _disposeAsync();
}

public sealed record AgentSessionRuntimeResources
{
    public string? WorkspacePath { get; init; }

    public ISandbox? Sandbox { get; init; }

    public ISessionToolApprovalCoordinator? ToolApprovalCoordinator { get; init; }

    public SessionToolApprovalPolicy? ToolApprovalPolicy { get; init; }
}
