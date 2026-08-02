using ChatClient.Application.Services.Sandbox;
using System.Collections.Immutable;

namespace ChatClient.Application.Services.Agentic;

public sealed class ToolApprovalScopeResolver
{
    private static readonly ImmutableHashSet<string> FileAccessTools =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            "file_access_read", "file_access_ls", "file_access_grep", "file_access_write",
            "file_access_replace", "file_access_replace_lines", "file_access_delete");

    public ToolApprovalSessionScope GetScope(string toolName) =>
        string.Equals(toolName, SandboxToolNames.RunShell, StringComparison.OrdinalIgnoreCase)
            ? ToolApprovalSessionScope.SandboxCommands
            : FileAccessTools.Contains(toolName)
                ? ToolApprovalSessionScope.FileAccess
                : ToolApprovalSessionScope.Tool;

    public string? GetWorkspace(ToolApprovalSessionScope scope, string? fileAccessWorkspace, string? sandboxWorkspace) =>
        scope switch
        {
            ToolApprovalSessionScope.FileAccess => fileAccessWorkspace,
            ToolApprovalSessionScope.SandboxCommands => sandboxWorkspace,
            _ => null
        };
}
