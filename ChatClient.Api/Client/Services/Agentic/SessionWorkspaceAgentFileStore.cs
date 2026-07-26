using Microsoft.Agents.AI;

#pragma warning disable MAAI001

namespace ChatClient.Api.Client.Services.Agentic;

/// <summary>Delegates every operation to one immutable framework store snapshot.</summary>
public sealed class SessionWorkspaceAgentFileStore : AgentFileStore
{
    private AgentFileStore _inner;

    public SessionWorkspaceAgentFileStore(string workspace)
    {
        WorkspacePath = ValidateWorkspace(workspace);
        _inner = new FileSystemAgentFileStore(WorkspacePath);
    }

    public string WorkspacePath { get; private set; }

    public void SetWorkspace(string workspace)
    {
        var normalized = ValidateWorkspace(workspace);
        Interlocked.Exchange(ref _inner, new FileSystemAgentFileStore(normalized));
        WorkspacePath = normalized;
    }

    public override Task WriteAsync(string path, string content, CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _inner).WriteAsync(path, content, cancellationToken);

    public override Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _inner).ReadAsync(path, cancellationToken);

    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _inner).DeleteAsync(path, cancellationToken);

    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(string directory, CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _inner).ListChildrenAsync(directory, cancellationToken);

    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _inner).FileExistsAsync(path, cancellationToken);

    public override Task<IReadOnlyList<FileSearchResult>> SearchAsync(string directory, string regexPattern, string? globPattern, bool recursive, CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _inner).SearchAsync(directory, regexPattern, globPattern, recursive, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _inner).CreateDirectoryAsync(path, cancellationToken);

    public static string ValidateWorkspace(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
            throw new InvalidOperationException("A workspace directory is required for File Access.");
        var normalized = Path.GetFullPath(workspace);
        if (!Directory.Exists(normalized))
            throw new InvalidOperationException($"Workspace directory does not exist: {normalized}");
        return normalized;
    }
}
#pragma warning restore MAAI001
