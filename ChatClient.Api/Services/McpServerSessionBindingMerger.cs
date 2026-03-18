using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public static class McpServerSessionBindingMerger
{
    public static IReadOnlyList<McpServerSessionBinding> Merge(
        IReadOnlyCollection<McpServerSessionBinding>? agentBindings,
        IReadOnlyCollection<McpServerSessionBinding>? sessionBindings)
    {
        Dictionary<string, McpServerSessionBinding> merged = new(StringComparer.OrdinalIgnoreCase);

        Apply(merged, agentBindings);
        Apply(merged, sessionBindings);

        return merged.Values
            .OrderBy(static binding => binding.GetIdentityKey(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void Apply(
        IDictionary<string, McpServerSessionBinding> merged,
        IReadOnlyCollection<McpServerSessionBinding>? bindings)
    {
        if (bindings is null)
        {
            return;
        }

        foreach (var binding in bindings)
        {
            if (binding is null || !binding.HasIdentity)
            {
                continue;
            }

            var key = binding.GetBindingKey();
            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = binding.Clone();
                continue;
            }

            if (binding.BindingId is Guid bindingId && bindingId != Guid.Empty)
            {
                existing.BindingId = bindingId;
            }

            if (binding.ServerId is Guid serverId && serverId != Guid.Empty)
            {
                existing.ServerId = serverId;
            }

            if (!string.IsNullOrWhiteSpace(binding.ServerName))
            {
                existing.ServerName = binding.ServerName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(binding.DisplayName))
            {
                existing.DisplayName = binding.DisplayName.Trim();
            }

            existing.Enabled = binding.Enabled;
            existing.SelectAllTools = binding.SelectAllTools;

            if (binding.SelectedTools.Count > 0 || !binding.SelectAllTools)
            {
                existing.SelectedTools = [.. binding.SelectedTools];
            }

            if (binding.Roots.Count > 0)
            {
                existing.Roots = [.. binding.Roots];
            }

            foreach (var (parameterKey, parameterValue) in binding.Parameters)
            {
                if (parameterValue is null)
                {
                    existing.Parameters.Remove(parameterKey);
                    continue;
                }

                existing.Parameters[parameterKey] = parameterValue;
            }
        }
    }
}
