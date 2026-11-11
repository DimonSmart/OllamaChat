using Microsoft.Agents.AI;

#pragma warning disable MAAI001

namespace ChatClient.Api.Client.Services.Agentic;

/// <summary>Delegates every operation to one immutable framework store snapshot.</summary>
public sealed class SessionWorkspaceAgentFileStore : AgentFileStore
{
    private AgentFileStore _inner;
    private readonly bool _strictWorkspaceBoundary;

    public SessionWorkspaceAgentFileStore(string workspace, bool strictWorkspaceBoundary = false)
    {
        WorkspacePath = ValidateWorkspace(workspace);
        _strictWorkspaceBoundary = strictWorkspaceBoundary;
        _inner = new FileSystemAgentFileStore(WorkspacePath);
    }

    public string WorkspacePath { get; private set; }

    public void SetWorkspace(string workspace)
    {
        if (_strictWorkspaceBoundary)
            throw new InvalidOperationException("The CLI workspace cannot be changed during execution.");
        var normalized = ValidateWorkspace(workspace);
        Interlocked.Exchange(ref _inner, new FileSystemAgentFileStore(normalized));
        WorkspacePath = normalized;
    }

    public override Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        CheckPath(path);
        return Volatile.Read(ref _inner).WriteAsync(path, content, cancellationToken);
    }

    public override Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        CheckPath(path);
        return Volatile.Read(ref _inner).ReadAsync(path, cancellationToken);
    }

    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        CheckPath(path);
        return Volatile.Read(ref _inner).DeleteAsync(path, cancellationToken);
    }

    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(string directory, CancellationToken cancellationToken = default)
    {
        CheckPath(directory);
        return Volatile.Read(ref _inner).ListChildrenAsync(directory, cancellationToken);
    }

    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        CheckPath(path);
        return Volatile.Read(ref _inner).FileExistsAsync(path, cancellationToken);
    }

    public override Task<IReadOnlyList<FileSearchResult>> SearchAsync(string directory, string regexPattern, string? globPattern, bool recursive, CancellationToken cancellationToken = default)
    {
        CheckPath(directory);
        return Volatile.Read(ref _inner).SearchAsync(directory, regexPattern, globPattern, recursive, cancellationToken);
    }

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        CheckPath(path);
        return Volatile.Read(ref _inner).CreateDirectoryAsync(path, cancellationToken);
    }

    private void CheckPath(string path)
    {
        if (!_strictWorkspaceBoundary)
            return;

        var root = WorkspacePath;
        var full = Path.GetFullPath(Path.IsPathFullyQualified(path)
            ? path : Path.Combine(root, path));
        var relative = Path.GetRelativePath(root, full);
        if (relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(relative))
            throw new UnauthorizedAccessException("File operation is outside the CLI workspace.");

        // Reject links and Windows reparse points anywhere in the path, including
        // intermediate directories. Do not trust a lexically in-workspace path.
        var cursor = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            cursor = Path.Combine(cursor, part);
            var info = Directory.Exists(cursor)
                ? (FileSystemInfo)new DirectoryInfo(cursor)
                : new FileInfo(cursor);
            if (info.LinkTarget is not null)
                throw new UnauthorizedAccessException("Symbolic links are not permitted in CLI File Access.");
            try
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Filesystem aliases are not permitted in CLI File Access.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

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
