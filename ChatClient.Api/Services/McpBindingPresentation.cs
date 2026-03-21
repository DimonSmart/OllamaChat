using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public static class McpBindingPresentation
{
    private static readonly string[] PreferredParameterKeys =
    [
        "sourceFile",
        "knowledgeFile",
        "file",
        "documentFile",
        "documentPath",
        "path",
        "uri",
        "url"
    ];

    public static string GetServerDisplayName(IMcpServerDescriptor serverDescriptor, McpServerSessionBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(serverDescriptor);

        var serverName = string.IsNullOrWhiteSpace(serverDescriptor.Name)
            ? "MCP Server"
            : serverDescriptor.Name.Trim();
        var bindingLabel = GetBindingLabel(binding);
        if (string.IsNullOrWhiteSpace(bindingLabel) ||
            string.Equals(bindingLabel, serverName, StringComparison.OrdinalIgnoreCase))
        {
            return serverName;
        }

        return $"{serverName} / {bindingLabel}";
    }

    public static string? GetBindingLabel(McpServerSessionBinding? binding)
    {
        if (!string.IsNullOrWhiteSpace(binding?.DisplayName))
        {
            return binding.DisplayName.Trim();
        }

        var resourceLabel = GetResourceLabel(binding);
        if (!string.IsNullOrWhiteSpace(resourceLabel))
        {
            return resourceLabel;
        }

        if (binding?.BindingId is Guid bindingId && bindingId != Guid.Empty)
        {
            return bindingId.ToString("N")[..8];
        }

        return null;
    }

    public static string BuildToolDescription(string? description, McpServerSessionBinding? binding)
    {
        var normalizedDescription = description?.Trim() ?? string.Empty;
        var context = GetBindingContextSummary(binding);
        if (string.IsNullOrWhiteSpace(context))
        {
            return normalizedDescription;
        }

        if (string.IsNullOrWhiteSpace(normalizedDescription))
        {
            return $"Binding context: {context}.";
        }

        if (normalizedDescription.Contains("Binding context:", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedDescription;
        }

        return $"{normalizedDescription} Binding context: {context}.";
    }

    public static string? GetBindingContextSummary(McpServerSessionBinding? binding)
    {
        if (binding is null)
        {
            return null;
        }

        List<string> parts = [];
        foreach (var (key, value) in binding.Parameters)
        {
            if (TryCreateParameterContext(key, value, out var context))
            {
                parts.Add(context);
            }
        }

        if (parts.Count == 0)
        {
            if (binding.Roots.Count == 1)
            {
                return $"root={SummarizeLocation(binding.Roots[0])}";
            }

            if (binding.Roots.Count > 1)
            {
                return $"roots={binding.Roots.Count}";
            }

            return null;
        }

        return string.Join("; ", parts.Distinct(StringComparer.OrdinalIgnoreCase).Take(2));
    }

    private static string? GetResourceLabel(McpServerSessionBinding? binding)
    {
        if (binding is null)
        {
            return null;
        }

        foreach (var preferredKey in PreferredParameterKeys)
        {
            if (binding.Parameters.TryGetValue(preferredKey, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return SummarizeLocation(value!);
            }
        }

        foreach (var (key, value) in binding.Parameters)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                LooksLikeResourceKey(key))
            {
                return SummarizeLocation(value!);
            }
        }

        if (binding.Roots.Count == 1)
        {
            return SummarizeLocation(binding.Roots[0]);
        }

        if (binding.Roots.Count > 1)
        {
            return $"{binding.Roots.Count} roots";
        }

        return null;
    }

    private static bool TryCreateParameterContext(string key, string? value, out string context)
    {
        context = string.Empty;
        if (string.IsNullOrWhiteSpace(key) ||
            string.IsNullOrWhiteSpace(value) ||
            !LooksLikeResourceKey(key))
        {
            return false;
        }

        context = $"{key.Trim()}={SummarizeLocation(value!)}";
        return true;
    }

    private static bool LooksLikeResourceKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = key.Trim();
        return normalized.Contains("file", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("path", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("uri", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("url", StringComparison.OrdinalIgnoreCase);
    }

    private static string SummarizeLocation(string raw)
    {
        var value = raw.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                return SummarizePath(uri.LocalPath);
            }

            var tail = uri.AbsolutePath.Trim('/');
            if (string.IsNullOrWhiteSpace(tail))
            {
                return uri.Host;
            }

            var segments = tail.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return segments.Length switch
            {
                0 => uri.Host,
                1 => $"{uri.Host}/{segments[0]}",
                _ => $"{uri.Host}/.../{segments[^1]}"
            };
        }

        if (value.Contains('\\') || value.Contains('/'))
        {
            return SummarizePath(value);
        }

        return value.Length <= 80 ? value : $"{value[..77]}...";
    }

    private static string SummarizePath(string path)
    {
        var normalized = path.Trim().Replace('/', '\\');
        var segments = normalized
            .Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (segments.Length == 0)
        {
            return normalized;
        }

        if (segments.Length == 1)
        {
            return segments[0];
        }

        return $"...\\{segments[^2]}\\{segments[^1]}";
    }
}
