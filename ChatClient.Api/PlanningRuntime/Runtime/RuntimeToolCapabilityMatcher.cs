using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;

namespace ChatClient.Api.PlanningRuntime.Runtime;

internal static class RuntimeToolCapabilityMatcher
{
    private static readonly string BuiltInWebServerName = BuiltInWebMcpServerTools.Descriptor.Name;

    public static bool IsBuiltInWebSearch(AppToolDescriptor? tool, string? capabilityId) =>
        MatchesBuiltInWebTool(tool, capabilityId, "search");

    public static bool IsBuiltInWebDownload(AppToolDescriptor? tool, string? capabilityId) =>
        MatchesBuiltInWebTool(tool, capabilityId, "download");

    private static bool MatchesBuiltInWebTool(AppToolDescriptor? tool, string? capabilityId, string toolName)
    {
        if (tool is not null)
        {
            if (string.Equals(tool.BaseQualifiedName, $"built-in-web:{toolName}", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tool.BaseQualifiedName, $"{BuiltInWebServerName}:{toolName}", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tool.QualifiedName, $"built-in-web:{toolName}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(tool.ToolName, toolName, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(tool.BaseServerName, "built-in-web", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(tool.BaseServerName, BuiltInWebServerName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(tool.ServerName, BuiltInWebServerName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return string.Equals(capabilityId, $"built-in-web:{toolName}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(capabilityId, $"{BuiltInWebServerName}:{toolName}", StringComparison.OrdinalIgnoreCase);
    }
}
