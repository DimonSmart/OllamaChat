using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public static class McpBindingToolSelectionResolver
{
    public static IReadOnlyList<AppToolDescriptor> FilterAvailableTools(
        IReadOnlyCollection<McpServerSessionBinding>? bindings,
        IReadOnlyCollection<AppToolDescriptor> availableTools)
    {
        ArgumentNullException.ThrowIfNull(availableTools);

        if (bindings is null || bindings.Count == 0)
        {
            return availableTools
                .OrderBy(static tool => tool.ServerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static tool => tool.ToolName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return availableTools
            .Where(tool => IsToolAllowed(tool, bindings))
            .OrderBy(static tool => tool.ServerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static tool => tool.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyCollection<string> ResolveQualifiedToolNames(
        IReadOnlyCollection<McpServerSessionBinding>? bindings,
        IReadOnlyCollection<AppToolDescriptor> availableTools)
    {
        ArgumentNullException.ThrowIfNull(availableTools);

        if (bindings is null || bindings.Count == 0 || availableTools.Count == 0)
        {
            return [];
        }

        return FilterAvailableTools(bindings, availableTools)
            .Select(static tool => tool.QualifiedName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsToolAllowed(
        AppToolDescriptor tool,
        IReadOnlyCollection<McpServerSessionBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        foreach (var binding in bindings.Where(static binding => binding.Enabled && binding.HasIdentity))
        {
            if (!ToolMatchesBinding(tool, binding))
            {
                continue;
            }

            if (binding.SelectAllTools)
            {
                return true;
            }

            if (binding.SelectedTools.Contains(tool.ToolName, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ToolMatchesBinding(AppToolDescriptor tool, McpServerSessionBinding binding)
    {
        if (binding.BindingId is Guid bindingId && bindingId != Guid.Empty)
        {
            return tool.BindingId == bindingId;
        }

        if (!string.IsNullOrWhiteSpace(binding.ServerName))
        {
            return string.Equals(tool.BaseServerName, binding.ServerName.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
