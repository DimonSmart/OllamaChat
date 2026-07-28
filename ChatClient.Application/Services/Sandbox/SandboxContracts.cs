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
    public required string SessionId { get; init; }

    public required string WorkspacePath { get; init; }

    public required string ProfileName { get; init; }
}

public sealed record SessionSandboxContext(
    Guid ProfileId,
    string ProfileName,
    string ProviderType,
    string Image,
    string WorkspacePath,
    SandboxState State);
