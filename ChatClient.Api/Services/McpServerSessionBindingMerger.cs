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

            var key = binding.GetIdentityKey();
            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = binding.Clone();
                continue;
            }

            if (binding.ServerId is Guid serverId && serverId != Guid.Empty)
            {
                existing.ServerId = serverId;
            }

            if (!string.IsNullOrWhiteSpace(binding.ServerName))
            {
                existing.ServerName = binding.ServerName.Trim();
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
