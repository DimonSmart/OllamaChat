namespace ChatClient.Api.Services.BuiltIn;

public sealed class UserProfilePreferencesEditorService
{
    public string StorageFilePath => UserProfilePrefsFileStore.FilePath;

    public Task<Dictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default) =>
        UserProfilePrefsFileStore.GetAllAsync(cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        UserProfilePrefsFileStore.ClearAsync(cancellationToken);

    public Task SaveAllAsync(
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        Dictionary<string, string> normalized = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenKeys = new(StringComparer.OrdinalIgnoreCase);

        foreach (var (rawKey, rawValue) in values)
        {
            var normalizedKey = rawKey?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedKey))
            {
                continue;
            }

            if (!seenKeys.Add(normalizedKey))
            {
                throw new InvalidOperationException($"Duplicate preference key '{normalizedKey}' is not allowed.");
            }

            normalized[normalizedKey] = rawValue?.Trim() ?? string.Empty;
        }

        return UserProfilePrefsFileStore.ReplaceAllAsync(normalized, cancellationToken);
    }
}
