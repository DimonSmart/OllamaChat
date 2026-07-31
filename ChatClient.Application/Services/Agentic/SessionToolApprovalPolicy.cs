namespace ChatClient.Application.Services.Agentic;

public sealed class SessionToolApprovalPolicy
{
    public const string SandboxRunShellToolName = "run_shell";
    private readonly HashSet<string> _tools = new(StringComparer.OrdinalIgnoreCase);
    private string? _fileAccessWorkspace;

    public ToolApprovalSessionScope GetScope(string toolName) =>
        string.Equals(toolName, SandboxRunShellToolName, StringComparison.OrdinalIgnoreCase)
            ? ToolApprovalSessionScope.SandboxCommands
            : toolName.StartsWith("file_access_", StringComparison.OrdinalIgnoreCase)
                ? ToolApprovalSessionScope.FileAccess
                : ToolApprovalSessionScope.Tool;

    public bool IsApproved(string toolName, string? workspacePath) => GetScope(toolName) switch
    {
        ToolApprovalSessionScope.SandboxCommands => _tools.Contains(SandboxRunShellToolName),
        ToolApprovalSessionScope.FileAccess => _fileAccessWorkspace is not null &&
            string.Equals(_fileAccessWorkspace, NormalizeWorkspacePath(workspacePath), WorkspaceComparison),
        ToolApprovalSessionScope.Tool => _tools.Contains(toolName),
        _ => false
    };

    public void Grant(string toolName, string? workspacePath)
    {
        switch (GetScope(toolName))
        {
            case ToolApprovalSessionScope.FileAccess:
                _fileAccessWorkspace = NormalizeWorkspacePath(workspacePath);
                break;
            case ToolApprovalSessionScope.SandboxCommands:
                _tools.Add(SandboxRunShellToolName);
                break;
            case ToolApprovalSessionScope.Tool:
                _tools.Add(toolName);
                break;
        }
    }

    public void ClearFileAccessGrant() => _fileAccessWorkspace = null;

    public static string? NormalizeWorkspacePath(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return null;
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
    }

    private static StringComparison WorkspaceComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
