using ChatClient.Application.Services;

namespace ChatClient.Api.Services;

public sealed class WorkspaceDirectoryService : IWorkspaceDirectoryService
{
    public IReadOnlyList<string> GetDirectories(string path)
    {
        var normalizedPath = NormalizeExistingDirectory(path);
        return Directory.EnumerateDirectories(normalizedPath)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string? GetParent(string path) => Directory.GetParent(NormalizePath(path))?.FullName;

    public IReadOnlyList<string> GetFileSystemRoots() => Directory.GetLogicalDrives();

    public string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A directory path is required.", nameof(path));

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    }

    public bool IsValidWorkspaceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || Path.IsPathRooted(name))
            return false;

        return name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\']) < 0 &&
               name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    public string CreateWorkspace(string workspacesRoot, string workspaceName)
    {
        if (!IsValidWorkspaceName(workspaceName))
            throw new ArgumentException("Enter a valid workspace name without path separators.", nameof(workspaceName));

        var normalizedRoot = NormalizePath(workspacesRoot);
        var targetPath = Path.GetFullPath(Path.Combine(normalizedRoot, workspaceName));
        var rootWithSeparator = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!targetPath.StartsWith(rootWithSeparator, comparison))
            throw new ArgumentException("The workspace must be created directly under the configured Workspaces root.", nameof(workspaceName));

        Directory.CreateDirectory(targetPath);
        return Path.TrimEndingDirectorySeparator(targetPath);
    }

    private string NormalizeExistingDirectory(string path)
    {
        var normalizedPath = NormalizePath(path);
        if (!Directory.Exists(normalizedPath))
            throw new DirectoryNotFoundException($"Directory '{normalizedPath}' was not found.");

        return normalizedPath;
    }
}
