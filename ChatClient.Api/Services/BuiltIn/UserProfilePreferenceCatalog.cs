namespace ChatClient.Api.Services.BuiltIn;

internal static class UserProfilePreferenceCatalog
{
    internal sealed record PreferenceSpec(
        string Key,
        string Label,
        string Prompt,
        string? DefaultValue = null,
        IReadOnlyList<string>? AllowedValues = null,
        string? Description = null);

    private static readonly PreferenceSpec[] _knownPreferences =
    [
        new(
            Key: "displayName",
            Label: "Display Name",
            Prompt: "How should I address you?",
            Description: "Preferred user name used for personalized addressing."),
        new(
            Key: "preferredLanguage",
            Label: "Preferred Language",
            Prompt: "Which language should I use by default when replying?",
            DefaultValue: "ru",
            AllowedValues: ["ru", "en", "es"],
            Description: "Default answer language."),
        new(
            Key: "tone",
            Label: "Tone",
            Prompt: "What communication tone do you prefer?",
            DefaultValue: "neutral",
            AllowedValues: ["neutral", "friendly", "formal"],
            Description: "Preferred communication tone."),
        new(
            Key: "verbosity",
            Label: "Verbosity",
            Prompt: "How detailed should responses be?",
            DefaultValue: "normal",
            AllowedValues: ["short", "normal", "detailed"],
            Description: "Preferred response detail level."),
        new(
            Key: "timezone",
            Label: "Timezone",
            Prompt: "Which time zone should be used for time-related information?",
            DefaultValue: "Europe/Madrid",
            Description: "Default time zone for time-related answers."),
        new(
            Key: "measurementSystem",
            Label: "Measurement System",
            Prompt: "Which measurement system should be used?",
            DefaultValue: "metric",
            AllowedValues: ["metric", "imperial"],
            Description: "Preferred measurement system."),
        new(
            Key: "grammarGenderRu",
            Label: "Russian Grammar Gender",
            Prompt: "Which grammatical gender forms should be used in Russian?",
            DefaultValue: "neutral",
            AllowedValues: ["male", "female", "neutral"],
            Description: "Preferred grammatical gender forms for Russian."),
        new(
            Key: "signature",
            Label: "Signature",
            Prompt: "What signature should be used in messages?",
            Description: "Optional signature for messages."),
        new(
            Key: "devEnvironment",
            Label: "Development Environment",
            Prompt: "What operating system do you use?",
            DefaultValue: "windows",
            AllowedValues: ["windows", "macos", "linux", "other"],
            Description: "Primary operating system."),
        new(
            Key: "editor",
            Label: "Editor",
            Prompt: "Which IDE or editor do you use?",
            DefaultValue: "vscode",
            AllowedValues: ["vs", "vscode", "rider", "other"],
            Description: "Preferred IDE or editor.")
    ];

    private static readonly IReadOnlyDictionary<string, PreferenceSpec> _knownPreferencesByKey =
        _knownPreferences.ToDictionary(static preference => preference.Key, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<PreferenceSpec> KnownPreferences => _knownPreferences;

    public static IReadOnlyDictionary<string, string> CanonicalKeyByAlias { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = "displayName",
            ["preferred_name"] = "displayName",
            ["preferredName"] = "displayName",
            ["userName"] = "displayName"
        };

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> LegacyAliasesByCanonicalKey { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["displayName"] = ["name", "preferred_name", "preferredName", "userName"]
        };

    public static PreferenceSpec GetSpecOrFallback(string key)
    {
        if (_knownPreferencesByKey.TryGetValue(key, out var spec))
            return spec;

        return new PreferenceSpec(
            Key: key,
            Label: key,
            Prompt: $"Enter a value for preference '{key}'.");
    }

    public static string NormalizeKey(string? key)
    {
        var normalizedKey = key?.Trim() ?? string.Empty;
        if (normalizedKey.Length == 0)
        {
            throw new InvalidOperationException("empty_key");
        }

        return CanonicalKeyByAlias.TryGetValue(normalizedKey, out var canonicalKey)
            ? canonicalKey
            : normalizedKey;
    }

    public static bool TryNormalizeValue(string key, string? rawValue, out string normalizedValue)
    {
        normalizedValue = string.Empty;
        if (rawValue is null)
            return false;

        var trimmed = rawValue.Trim();
        if (trimmed.Length == 0)
            return false;

        if (_knownPreferencesByKey.TryGetValue(key, out var spec) && spec.AllowedValues is { Count: > 0 })
        {
            foreach (var allowedValue in spec.AllowedValues)
            {
                if (!string.Equals(allowedValue, trimmed, StringComparison.OrdinalIgnoreCase))
                    continue;

                normalizedValue = allowedValue;
                return true;
            }

            return false;
        }

        normalizedValue = trimmed;
        return true;
    }

    public static bool TryGetStoredValue(
        IReadOnlyDictionary<string, string> storedValues,
        string normalizedKey,
        out string value)
    {
        if (storedValues.TryGetValue(normalizedKey, out var directValue) &&
            !string.IsNullOrWhiteSpace(directValue))
        {
            value = directValue;
            return true;
        }

        if (LegacyAliasesByCanonicalKey.TryGetValue(normalizedKey, out var aliases))
        {
            foreach (var alias in aliases)
            {
                if (storedValues.TryGetValue(alias, out var aliasValue) &&
                    !string.IsNullOrWhiteSpace(aliasValue))
                {
                    value = aliasValue;
                    return true;
                }
            }
        }

        value = string.Empty;
        return false;
    }
}
