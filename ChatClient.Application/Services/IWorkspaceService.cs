using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface IWorkspaceService
{
    Task<IReadOnlyList<WorkspaceDefinition>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<WorkspaceDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<WorkspaceDefinition> CreateAsync(WorkspaceDefinition definition, CancellationToken cancellationToken = default);
    Task UpdateAsync(WorkspaceDefinition definition, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, long revision, CancellationToken cancellationToken = default);
}

public sealed class WorkspaceException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed record WorkspaceSelection(Guid? WorkspaceId = null, string? DirectPath = null);

public sealed record ResolvedWorkspace(Guid? SourceWorkspaceId, string Path);

public interface IWorkspaceResolver
{
    Task<ResolvedWorkspace?> ResolveAsync(
        WorkspaceSelection selection, CancellationToken cancellationToken = default);
}
