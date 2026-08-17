namespace ChatClient.Application.Services;

public interface IWorkspaceDirectoryService
{
    IReadOnlyList<string> GetDirectories(string path);
    string? GetParent(string path);
    IReadOnlyList<string> GetFileSystemRoots();
    string NormalizePath(string path);
    bool IsValidWorkspaceName(string? name);
    string CreateWorkspace(string workspacesRoot, string workspaceName);
}
