using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class WorkspaceService(
    IWorkspaceRepository repository,
    IWorkspaceDirectoryService directoryService,
    TimeProvider timeProvider) : IWorkspaceService
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public Task<IReadOnlyList<WorkspaceDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
        => repository.GetAllAsync(cancellationToken);

    public async Task<WorkspaceDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => (await repository.GetAllAsync(cancellationToken)).FirstOrDefault(item => item.Id == id)?.Clone();

    public Task<WorkspaceDefinition> CreateAsync(WorkspaceDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var name = NormalizeName(definition.Name);
        var path = NormalizeExistingPath(definition.Path);
        var now = timeProvider.GetUtcNow();
        return repository.UpdateAsync(items =>
        {
            CheckUniqueness(items, name, path, Guid.Empty);
            var saved = new WorkspaceDefinition
            {
                Id = Guid.NewGuid(), Name = name, Path = path,
                CreatedAtUtc = now, UpdatedAtUtc = now, Revision = 1
            };
            items.Add(saved);
            return saved.Clone();
        }, cancellationToken);
    }

    public async Task UpdateAsync(WorkspaceDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Id == Guid.Empty)
            throw new WorkspaceException("workspace_selection_invalid", "Workspace ID cannot be empty.");
        var name = NormalizeName(definition.Name);
        var path = NormalizeExistingPath(definition.Path);
        var now = timeProvider.GetUtcNow();
        await repository.UpdateAsync(items =>
        {
            var existing = items.FirstOrDefault(item => item.Id == definition.Id)
                ?? throw new WorkspaceException("workspace_not_found", "Saved workspace was deleted.");
            CheckRevision(existing, definition.Revision);
            CheckUniqueness(items, name, path, definition.Id);
            existing.Name = name;
            existing.Path = path;
            existing.Revision++;
            existing.UpdatedAtUtc = now < existing.CreatedAtUtc ? existing.CreatedAtUtc : now;
            return true;
        }, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, long revision, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new WorkspaceException("workspace_selection_invalid", "Workspace ID cannot be empty.");
        await repository.UpdateAsync(items =>
        {
            var existing = items.FirstOrDefault(item => item.Id == id)
                ?? throw new WorkspaceException("workspace_not_found", "Saved workspace was deleted.");
            CheckRevision(existing, revision);
            items.Remove(existing);
            return true;
        }, cancellationToken);
    }

    private static void CheckRevision(WorkspaceDefinition existing, long revision)
    {
        if (existing.Revision != revision)
            throw new WorkspaceException("workspace_revision_conflict",
                "This workspace was changed in another window. Reload before saving.");
    }

    private static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new WorkspaceException("workspace_selection_invalid", "Workspace name is required.");
        return trimmed;
    }

    private string NormalizeExistingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
            throw new WorkspaceException("workspace_path_invalid", "Saved workspace needs a fully qualified path.");
        try
        {
            var normalized = directoryService.NormalizePath(path);
            if (!Directory.Exists(normalized))
                throw new WorkspaceException("workspace_unavailable", "Workspace directory does not exist.");
            // Opening the directory also detects permission and many availability errors.
            using var enumerator = Directory.EnumerateFileSystemEntries(normalized).GetEnumerator();
            enumerator.MoveNext();
            return normalized;
        }
        catch (WorkspaceException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new WorkspaceException("workspace_path_invalid", "Workspace path is invalid.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkspaceException("workspace_unavailable", "Workspace directory is not accessible.");
        }
    }

    private static void CheckUniqueness(List<WorkspaceDefinition> items, string name, string path, Guid skip)
    {
        if (items.Any(item => item.Id != skip &&
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new WorkspaceException("workspace_selection_invalid", "Workspace name is already registered.");
        if (items.Any(item => item.Id != skip && PathComparer.Equals(item.Path, path)))
            throw new WorkspaceException("workspace_selection_invalid", "Workspace directory is already registered.");
    }
}

public sealed class WorkspaceResolver(
    IWorkspaceService workspaces,
    IWorkspaceDirectoryService directories) : IWorkspaceResolver
{
    public async Task<ResolvedWorkspace?> ResolveAsync(
        WorkspaceSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.WorkspaceId.HasValue && selection.DirectPath is not null)
            throw new WorkspaceException("workspace_selection_invalid", "Choose a saved workspace or a direct path, not both.");
        if (selection.WorkspaceId == Guid.Empty)
            throw new WorkspaceException("workspace_selection_invalid", "Workspace ID cannot be empty.");

        string? path = selection.DirectPath;
        if (selection.WorkspaceId is { } id)
        {
            var definition = await workspaces.GetByIdAsync(id, cancellationToken)
                ?? throw new WorkspaceException("workspace_not_found", "Saved workspace was deleted.");
            path = definition.Path;
        }
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (!Path.IsPathFullyQualified(path.Trim()))
            throw new WorkspaceException("workspace_path_invalid", "Workspace path must be fully qualified.");
        try
        {
            var normalized = directories.NormalizePath(path);
            if (!Directory.Exists(normalized))
                throw new WorkspaceException("workspace_unavailable", "Workspace directory does not exist.");
            using var enumerator = Directory.EnumerateFileSystemEntries(normalized).GetEnumerator();
            enumerator.MoveNext();
            return new ResolvedWorkspace(selection.WorkspaceId, normalized);
        }
        catch (WorkspaceException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new WorkspaceException("workspace_path_invalid", "Workspace path is invalid.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkspaceException("workspace_unavailable", "Workspace directory is not accessible.");
        }
    }
}
