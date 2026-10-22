using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public sealed class WorkspaceRepository : IWorkspaceRepository
{
    private readonly JsonFileRepository<List<WorkspaceDefinition>> _repo;

    public WorkspaceRepository(IConfiguration configuration, ILogger<WorkspaceRepository> logger)
    {
        var path = StoragePathResolver.ResolveUserPath(configuration,
            configuration["Workspaces:FilePath"], FilePathConstants.DefaultWorkspacesFile);
        _repo = new JsonFileRepository<List<WorkspaceDefinition>>(
            path, logger, atomicWrites: true, rejectNullRead: true, deferDirectoryCreation: true);
    }

    public async Task<IReadOnlyList<WorkspaceDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _repo.ReadAsync(cancellationToken) ?? [];
        ValidateDocument(entries);
        return entries.Select(static entry => entry.Clone()).ToArray();
    }

    public async Task<TResult> UpdateAsync<TResult>(
        Func<List<WorkspaceDefinition>, TResult> mutation, CancellationToken cancellationToken = default)
    {
        TResult result = default!;
        await _repo.UpdateAsync(entries =>
        {
            ValidateDocument(entries);
            result = mutation(entries);
            ValidateDocument(entries);
            return Task.CompletedTask;
        }, [], cancellationToken);
        return result;
    }

    private static void ValidateDocument(List<WorkspaceDefinition> entries)
    {
        if (entries.Any(static x => x is null))
            throw new InvalidDataException("Workspaces JSON contains a null record.");
        var nameComparer = StringComparer.OrdinalIgnoreCase;
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (entries.Any(static x => x.Id == Guid.Empty || x.Revision < 1 ||
            string.IsNullOrWhiteSpace(x.Name) || string.IsNullOrWhiteSpace(x.Path) ||
            x.CreatedAtUtc == default || x.UpdatedAtUtc == default ||
            x.UpdatedAtUtc < x.CreatedAtUtc))
            throw new InvalidDataException("Workspaces JSON contains invalid workspace metadata.");
        if (entries.Any(x => !System.IO.Path.IsPathFullyQualified(x.Path) ||
            x.Path != System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(x.Path)) ||
            x.Name != x.Name.Trim()))
            throw new InvalidDataException("Workspaces JSON contains unnormalized names or paths.");
        if (entries.Select(static x => x.Id).Distinct().Count() != entries.Count ||
            entries.Select(static x => x.Name).Distinct(nameComparer).Count() != entries.Count ||
            entries.Select(static x => x.Path).Distinct(pathComparer).Count() != entries.Count)
            throw new InvalidDataException("Workspaces JSON contains duplicate workspace IDs, names, or paths.");
    }
}
