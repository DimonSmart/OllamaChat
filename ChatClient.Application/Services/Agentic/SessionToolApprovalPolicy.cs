using ChatClient.Application.Services.Sandbox;

namespace ChatClient.Application.Services.Agentic;

public sealed class SessionToolApprovalPolicy
{
    private readonly HashSet<string> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolApprovalScopeResolver _scopeResolver;
    private string? _currentWorkspace;
    private string? _approvedFileAccessWorkspace;

    public SessionToolApprovalPolicy(ToolApprovalScopeResolver? scopeResolver = null) =>
        _scopeResolver = scopeResolver ?? new ToolApprovalScopeResolver();

    public ToolApprovalSessionScope GetScope(string toolName) => _scopeResolver.GetScope(toolName);

    public bool IsApproved(string toolName) => GetScope(toolName) switch
    {
        ToolApprovalSessionScope.SandboxCommands => _tools.Contains(SandboxToolNames.RunShell),
        ToolApprovalSessionScope.FileAccess => _approvedFileAccessWorkspace is not null &&
            string.Equals(_approvedFileAccessWorkspace, _currentWorkspace, WorkspaceComparison),
        ToolApprovalSessionScope.Tool => _tools.Contains(toolName),
        _ => false
    };

    public void SetWorkspace(string? workspace)
    {
        var normalized = NormalizeWorkspacePath(workspace);
        if (!string.Equals(_currentWorkspace, normalized, WorkspaceComparison))
            _approvedFileAccessWorkspace = null;
        _currentWorkspace = normalized;
    }

    public void Grant(string toolName)
    {
        switch (GetScope(toolName))
        {
            case ToolApprovalSessionScope.FileAccess:
                _approvedFileAccessWorkspace = _currentWorkspace;
                break;
            case ToolApprovalSessionScope.SandboxCommands:
                _tools.Add(SandboxToolNames.RunShell);
                break;
            case ToolApprovalSessionScope.Tool:
                _tools.Add(toolName);
                break;
        }
    }

    public void ClearFileAccessGrant() => _approvedFileAccessWorkspace = null;

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
