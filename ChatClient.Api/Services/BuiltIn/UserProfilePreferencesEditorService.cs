using ChatClient.Api.Services;

namespace ChatClient.Api.Services.BuiltIn;

public sealed class UserProfilePreferencesEditorService(
    IMcpClientService mcpClientService,
    McpFunctionIndexService mcpFunctionIndexService)
{
    private readonly IMcpClientService _mcpClientService = mcpClientService;
    private readonly McpFunctionIndexService _mcpFunctionIndexService = mcpFunctionIndexService;

    public string StorageFilePath => UserProfilePreferencesStore.FilePath;

    public Task<UserProfilePreferencesDocument> GetDocumentAsync(CancellationToken cancellationToken = default) =>
        UserProfilePreferencesStore.GetAsync(cancellationToken);

    public UserProfilePreferencesDocument CreateDefaultDocument() =>
        UserProfilePreferencesDocument.CreateDefault();

    public async Task SaveAsync(
        UserProfilePreferencesDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        ValidateDocument(document);

        await UserProfilePreferencesStore.SaveAsync(document, cancellationToken);
        await _mcpClientService.InvalidateAsync(cancellationToken);
        _mcpFunctionIndexService.Invalidate();
    }

    private static void ValidateDocument(UserProfilePreferencesDocument document)
    {
        HashSet<string> seenKeys = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> reservedNames = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenAliases = new(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in document.Definitions)
        {
            var key = definition.Key?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException("Preference definition key is required.");
            }

            if (!seenKeys.Add(key))
            {
                throw new InvalidOperationException($"Duplicate preference key '{key}' is not allowed.");
            }

            reservedNames.Add(key);
        }

        foreach (var definition in document.Definitions)
        {
            var key = definition.Key.Trim();
            foreach (var alias in definition.Aliases)
            {
                var normalizedAlias = alias?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(normalizedAlias))
                {
                    continue;
                }

                if (reservedNames.Contains(normalizedAlias))
                {
                    throw new InvalidOperationException(
                        $"Alias '{normalizedAlias}' conflicts with a configured preference key.");
                }

                if (!seenAliases.Add(normalizedAlias))
                {
                    throw new InvalidOperationException($"Duplicate alias '{normalizedAlias}' is not allowed.");
                }
            }
        }

        foreach (var (rawKey, _) in document.Values)
        {
            var key = rawKey?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException("Stored value key is required.");
            }

            if (!seenKeys.Contains(key))
            {
                throw new InvalidOperationException(
                    $"Stored value key '{key}' is not defined in the configured preference definitions.");
            }
        }
    }
}
