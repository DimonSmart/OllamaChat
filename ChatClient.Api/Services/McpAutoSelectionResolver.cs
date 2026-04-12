namespace ChatClient.Api.Services;

internal static class McpAutoSelectionResolver
{
    private static readonly string[] GreetingPrefixes =
    [
        "hi",
        "hello",
        "hey",
        "\u043f\u0440\u0438\u0432\u0435\u0442",
        "\u0437\u0434\u0440\u0430\u0432\u0441\u0442\u0432\u0443\u0439",
        "\u0437\u0434\u0440\u0430\u0432\u0441\u0442\u0432\u0443\u0439\u0442\u0435",
        "\u0434\u043e\u0431\u0440\u043e\u0435 \u0443\u0442\u0440\u043e",
        "\u0434\u043e\u0431\u0440\u044b\u0439 \u0434\u0435\u043d\u044c",
        "\u0434\u043e\u0431\u0440\u044b\u0439 \u0432\u0435\u0447\u0435\u0440",
        "hola",
        "buenos dias",
        "buenas tardes",
        "buenas noches"
    ];

    public static IReadOnlyList<string> ResolveQualifiedFunctionNames(
        IReadOnlyList<string> selectedFunctions,
        IReadOnlyCollection<AppToolDescriptor> availableTools,
        string? userQuery,
        int maxCount)
    {
        List<string> ordered = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var selected in selectedFunctions)
        {
            foreach (var tool in availableTools)
            {
                if (!MatchesSelection(selected, tool))
                {
                    continue;
                }

                if (seen.Add(tool.QualifiedName))
                {
                    ordered.Add(tool.QualifiedName);
                }
            }
        }

        PromotePersonalizationToolIfNeeded(ordered, availableTools, userQuery);

        if (maxCount > 0 && ordered.Count > maxCount)
        {
            ordered = ordered.Take(maxCount).ToList();
        }

        return ordered;
    }

    private static void PromotePersonalizationToolIfNeeded(
        List<string> ordered,
        IReadOnlyCollection<AppToolDescriptor> availableTools,
        string? userQuery)
    {
        if (!IsGreetingLikeQuery(userQuery))
        {
            return;
        }

        var candidate = availableTools.FirstOrDefault(IsPersonalizationCandidate);
        if (candidate is null)
        {
            return;
        }

        ordered.RemoveAll(name => string.Equals(name, candidate.QualifiedName, StringComparison.OrdinalIgnoreCase));
        ordered.Insert(0, candidate.QualifiedName);
    }

    private static bool MatchesSelection(string selected, AppToolDescriptor tool) =>
        string.Equals(selected, tool.BaseQualifiedName ?? tool.QualifiedName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(selected, tool.QualifiedName, StringComparison.OrdinalIgnoreCase);

    private static bool IsPersonalizationCandidate(AppToolDescriptor tool)
    {
        if (string.Equals(tool.ToolName, "prefs_get", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var searchable = $"{tool.ServerName} {tool.DisplayName} {tool.Description}";
        return searchable.Contains("current user", StringComparison.OrdinalIgnoreCase) &&
               searchable.Contains("display name", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGreetingLikeQuery(string? userQuery)
    {
        var normalized = NormalizeQuery(userQuery);
        if (normalized.Length == 0)
        {
            return false;
        }

        return GreetingPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string NormalizeQuery(string? userQuery)
    {
        if (string.IsNullOrWhiteSpace(userQuery))
        {
            return string.Empty;
        }

        return new string(userQuery
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) ? ch : ' ')
            .ToArray());
    }
}
