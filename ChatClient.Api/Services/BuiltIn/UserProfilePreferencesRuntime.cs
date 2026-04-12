using System.Text;
using System.Text.Json;

namespace ChatClient.Api.Services.BuiltIn;

internal sealed class UserProfilePreferencesSnapshot
{
    private readonly Dictionary<string, string> _values;
    private readonly Dictionary<string, UserProfilePreferenceDefinition> _definitionsByKey;
    private readonly Dictionary<string, string> _canonicalKeyByAlias;

    public UserProfilePreferencesSnapshot(
        string serverDescription,
        IReadOnlyList<UserProfilePreferenceDefinition> definitions,
        Dictionary<string, UserProfilePreferenceDefinition> definitionsByKey,
        Dictionary<string, string> canonicalKeyByAlias,
        Dictionary<string, string> values)
    {
        ServerDescription = serverDescription;
        Definitions = definitions;
        _definitionsByKey = definitionsByKey;
        _canonicalKeyByAlias = canonicalKeyByAlias;
        _values = values;
        SupportedKeys = definitions
            .Select(static definition => definition.Key)
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        AcceptedKeys = _definitionsByKey.Keys
            .Concat(_canonicalKeyByAlias.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string ServerDescription { get; }

    public IReadOnlyList<UserProfilePreferenceDefinition> Definitions { get; }

    public IReadOnlyList<string> SupportedKeys { get; }

    public IReadOnlyList<string> AcceptedKeys { get; }

    public IReadOnlyDictionary<string, string> Values => _values;

    public bool TryResolveKey(string? rawKey, out string canonicalKey)
    {
        var normalizedKey = rawKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            canonicalKey = string.Empty;
            return false;
        }

        if (_definitionsByKey.ContainsKey(normalizedKey))
        {
            canonicalKey = normalizedKey;
            return true;
        }

        if (_canonicalKeyByAlias.TryGetValue(normalizedKey, out var aliasKey))
        {
            canonicalKey = aliasKey;
            return true;
        }

        canonicalKey = string.Empty;
        return false;
    }

    public bool TryGetDefinition(string key, out UserProfilePreferenceDefinition definition) =>
        _definitionsByKey.TryGetValue(key, out definition!);

    public bool TryNormalizeValue(string key, string? rawValue, out string normalizedValue)
    {
        normalizedValue = string.Empty;
        if (!_definitionsByKey.TryGetValue(key, out var definition) || rawValue is null)
        {
            return false;
        }

        var trimmedValue = rawValue.Trim();
        if (trimmedValue.Length == 0)
        {
            return false;
        }

        if (definition.AllowedValues.Count == 0)
        {
            normalizedValue = trimmedValue;
            return true;
        }

        foreach (var allowedValue in definition.AllowedValues)
        {
            if (!string.Equals(allowedValue, trimmedValue, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            normalizedValue = allowedValue;
            return true;
        }

        return false;
    }

    public bool TryGetStoredValue(string key, out string value) =>
        _values.TryGetValue(key, out value!);
}

internal static class UserProfilePreferencesRuntime
{
    public static UserProfilePreferencesDocument NormalizeDocument(
        UserProfilePreferencesDocument? document,
        bool useDefaultWhenMissing)
    {
        var source = document ?? (useDefaultWhenMissing ? UserProfilePreferencesDocument.CreateDefault() : new UserProfilePreferencesDocument());
        var normalizedDefinitions = NormalizeDefinitions(source.Definitions);
        var snapshot = CreateSnapshot(
            serverDescription: source.ServerDescription,
            normalizedDefinitions: normalizedDefinitions,
            rawValues: source.Values);

        return new UserProfilePreferencesDocument
        {
            ServerDescription = source.ServerDescription?.Trim() ?? string.Empty,
            Definitions = normalizedDefinitions
                .Select(CloneDefinition)
                .ToList(),
            Values = new Dictionary<string, string>(snapshot.Values, StringComparer.OrdinalIgnoreCase)
        };
    }

    public static UserProfilePreferencesSnapshot CreateSnapshot(UserProfilePreferencesDocument? document, bool useDefaultWhenMissing = true)
    {
        var normalized = NormalizeDocument(document, useDefaultWhenMissing);
        return CreateSnapshot(
            normalized.ServerDescription,
            normalized.Definitions,
            normalized.Values);
    }

    public static string BuildServerDescription(UserProfilePreferencesDocument? document) =>
        BuildServerDescription(CreateSnapshot(document));

    public static string BuildServerDescription(UserProfilePreferencesSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ServerDescription))
        {
            return snapshot.ServerDescription.Trim();
        }

        var configuredFieldsSummary = BuildFieldSummary(snapshot.Definitions);
        if (configuredFieldsSummary.Length == 0)
        {
            return $"{UserProfilePreferencesDocument.DefaultServerDescription} No profile fields are configured yet.";
        }

        return $"{UserProfilePreferencesDocument.DefaultServerDescription} Configured fields: {configuredFieldsSummary}.";
    }

    public static string BuildPrefsGetDescription(UserProfilePreferencesSnapshot snapshot)
    {
        var baseDescription = BuildServerDescription(snapshot);
        var personalizationHint = BuildPersonalizationHint(snapshot);
        var fieldSummary = BuildFieldSummary(snapshot.Definitions);

        if (string.IsNullOrWhiteSpace(fieldSummary))
        {
            return $"{baseDescription} {personalizationHint} Gets one configured current-user profile value by key. If the value is missing, asks the user via elicitation, validates it against the configured field definition, saves it, and returns it.";
        }

        return $"{baseDescription} {personalizationHint} Supported fields: {fieldSummary}. Gets one configured current-user profile value by key. If the value is missing, asks the user via elicitation, validates it against the configured field definition, saves it, and returns it.";
    }

    public static string BuildPrefsGetAllDescription(UserProfilePreferencesSnapshot snapshot)
    {
        var baseDescription = BuildServerDescription(snapshot);
        var personalizationHint = BuildPersonalizationHint(snapshot);
        var fieldSummary = BuildFieldSummary(snapshot.Definitions);

        if (string.IsNullOrWhiteSpace(fieldSummary))
        {
            return $"{baseDescription} {personalizationHint} Returns the configured field definitions together with all currently stored values.";
        }

        return $"{baseDescription} {personalizationHint} Configured fields: {fieldSummary}. Returns the configured field definitions together with all currently stored values.";
    }

    public static string BuildPrefsResetAllDescription() =>
        "Clears all stored user profile values but preserves the configured field definitions. If confirm is false, asks the user for confirmation first.";

    public static string BuildKeyParameterDescription(UserProfilePreferencesSnapshot snapshot)
    {
        if (snapshot.AcceptedKeys.Count == 0)
        {
            return "Configured preference key. No profile fields are configured yet.";
        }

        var supportedKeys = string.Join(", ", snapshot.SupportedKeys);
        var aliases = snapshot.AcceptedKeys.Except(snapshot.SupportedKeys, StringComparer.OrdinalIgnoreCase).ToArray();
        if (aliases.Length == 0)
        {
            return $"Configured preference key. Supported keys: {supportedKeys}.";
        }

        return $"Configured preference key. Supported keys: {supportedKeys}. Accepted aliases: {string.Join(", ", aliases)}.";
    }

    public static JsonElement BuildPrefsGetInputSchema(UserProfilePreferencesSnapshot snapshot)
    {
        Dictionary<string, object?> keySchema = new(StringComparer.Ordinal)
        {
            ["type"] = "string",
            ["description"] = BuildKeyParameterDescription(snapshot)
        };

        if (snapshot.AcceptedKeys.Count > 0)
        {
            keySchema["enum"] = snapshot.AcceptedKeys;
        }

        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object?>
            {
                ["key"] = keySchema
            },
            required = new[] { "key" }
        });
    }

    public static JsonElement BuildPrefsGetOutputSchema() =>
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object?>
            {
                ["key"] = new { type = "string" },
                ["exists"] = new { type = "boolean" },
                ["value"] = new { type = "string" },
                ["source"] = new
                {
                    type = "string",
                    @enum = new[] { "stored", "elicited" }
                }
            },
            required = new[] { "key", "exists", "value", "source" }
        });

    public static JsonElement BuildPrefsGetAllOutputSchema() =>
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object?>
            {
                ["serverDescription"] = new { type = "string" },
                ["supportedKeys"] = new
                {
                    type = "array",
                    items = new { type = "string" }
                },
                ["acceptedKeys"] = new
                {
                    type = "array",
                    items = new { type = "string" }
                },
                ["definitions"] = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object?>
                        {
                            ["key"] = new { type = "string" },
                            ["description"] = new { type = "string" },
                            ["prompt"] = new { type = "string" },
                            ["defaultValue"] = new { type = new[] { "string", "null" } },
                            ["allowedValues"] = new
                            {
                                type = "array",
                                items = new { type = "string" }
                            },
                            ["aliases"] = new
                            {
                                type = "array",
                                items = new { type = "string" }
                            }
                        },
                        required = new[] { "key", "description", "prompt", "allowedValues", "aliases" }
                    }
                },
                ["values"] = new
                {
                    type = "object",
                    additionalProperties = new { type = "string" }
                }
            },
            required = new[] { "serverDescription", "supportedKeys", "acceptedKeys", "definitions", "values" }
        });

    public static JsonElement BuildPrefsResetAllInputSchema() =>
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object?>
            {
                ["confirm"] = new
                {
                    type = "boolean",
                    description = "When true, reset happens without additional user confirmation."
                }
            }
        });

    public static JsonElement BuildPrefsResetAllOutputSchema() =>
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object?>
            {
                ["cleared"] = new { type = "boolean" }
            },
            required = new[] { "cleared" }
        });

    private static UserProfilePreferencesSnapshot CreateSnapshot(
        string? serverDescription,
        IReadOnlyList<UserProfilePreferenceDefinition> normalizedDefinitions,
        IReadOnlyDictionary<string, string>? rawValues)
    {
        Dictionary<string, UserProfilePreferenceDefinition> definitionsByKey = new(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in normalizedDefinitions)
        {
            definitionsByKey[definition.Key] = CloneDefinition(definition);
        }

        HashSet<string> reservedNames = new(definitionsByKey.Keys, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> canonicalKeyByAlias = new(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in normalizedDefinitions)
        {
            foreach (var alias in definition.Aliases)
            {
                if (reservedNames.Contains(alias) || canonicalKeyByAlias.ContainsKey(alias))
                {
                    continue;
                }

                canonicalKeyByAlias[alias] = definition.Key;
            }
        }

        Dictionary<string, string> normalizedValues = new(StringComparer.OrdinalIgnoreCase);
        if (rawValues is not null)
        {
            foreach (var (rawKey, rawValue) in rawValues)
            {
                var normalizedKey = ResolveKey(rawKey, definitionsByKey, canonicalKeyByAlias);
                if (normalizedKey is null)
                {
                    continue;
                }

                if (!TryNormalizeValue(definitionsByKey[normalizedKey], rawValue, out var normalizedValue))
                {
                    continue;
                }

                normalizedValues[normalizedKey] = normalizedValue;
            }
        }

        return new UserProfilePreferencesSnapshot(
            serverDescription?.Trim() ?? string.Empty,
            normalizedDefinitions.Select(CloneDefinition).ToArray(),
            definitionsByKey,
            canonicalKeyByAlias,
            normalizedValues);
    }

    private static List<UserProfilePreferenceDefinition> NormalizeDefinitions(IEnumerable<UserProfilePreferenceDefinition>? definitions)
    {
        List<UserProfilePreferenceDefinition> normalized = [];
        HashSet<string> seenKeys = new(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions ?? [])
        {
            var key = definition.Key?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key) || !seenKeys.Add(key))
            {
                continue;
            }

            var allowedValues = NormalizeStringList(definition.AllowedValues);
            var defaultValue = NormalizeOptionalValue(definition.DefaultValue);
            if (defaultValue is not null && allowedValues.Count > 0)
            {
                defaultValue = allowedValues.FirstOrDefault(value =>
                    string.Equals(value, defaultValue, StringComparison.OrdinalIgnoreCase));
            }

            var aliases = NormalizeStringList(definition.Aliases)
                .Where(alias => !string.Equals(alias, key, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            normalized.Add(new UserProfilePreferenceDefinition
            {
                Key = key,
                Description = definition.Description?.Trim() ?? string.Empty,
                Prompt = string.IsNullOrWhiteSpace(definition.Prompt)
                    ? $"Enter a value for preference '{key}'."
                    : definition.Prompt.Trim(),
                DefaultValue = defaultValue,
                AllowedValues = allowedValues,
                Aliases = aliases
            });
        }

        return normalized;
    }

    private static List<string> NormalizeStringList(IEnumerable<string>? values)
    {
        List<string> normalized = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (var value in values ?? [])
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed) || !seen.Add(trimmed))
            {
                continue;
            }

            normalized.Add(trimmed);
        }

        return normalized;
    }

    private static string? NormalizeOptionalValue(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? ResolveKey(
        string? rawKey,
        IReadOnlyDictionary<string, UserProfilePreferenceDefinition> definitionsByKey,
        IReadOnlyDictionary<string, string> canonicalKeyByAlias)
    {
        var normalizedKey = rawKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return null;
        }

        if (definitionsByKey.ContainsKey(normalizedKey))
        {
            return normalizedKey;
        }

        return canonicalKeyByAlias.GetValueOrDefault(normalizedKey);
    }

    private static bool TryNormalizeValue(
        UserProfilePreferenceDefinition definition,
        string? rawValue,
        out string normalizedValue)
    {
        normalizedValue = string.Empty;
        if (rawValue is null)
        {
            return false;
        }

        var trimmedValue = rawValue.Trim();
        if (trimmedValue.Length == 0)
        {
            return false;
        }

        if (definition.AllowedValues.Count == 0)
        {
            normalizedValue = trimmedValue;
            return true;
        }

        foreach (var allowedValue in definition.AllowedValues)
        {
            if (!string.Equals(allowedValue, trimmedValue, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            normalizedValue = allowedValue;
            return true;
        }

        return false;
    }

    private static UserProfilePreferenceDefinition CloneDefinition(UserProfilePreferenceDefinition definition) =>
        new()
        {
            Key = definition.Key,
            Description = definition.Description,
            Prompt = definition.Prompt,
            DefaultValue = definition.DefaultValue,
            AllowedValues = definition.AllowedValues.ToList(),
            Aliases = definition.Aliases.ToList()
        };

    private static string BuildFieldSummary(IEnumerable<UserProfilePreferenceDefinition> definitions)
    {
        StringBuilder builder = new();
        foreach (var definition in definitions.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(definition.Key);
            if (!string.IsNullOrWhiteSpace(definition.Description))
            {
                builder.Append(": ");
                builder.Append(definition.Description.Trim());
            }
        }

        return builder.ToString();
    }

    private static string BuildPersonalizationHint(UserProfilePreferencesSnapshot snapshot)
    {
        var displayNameDefinition = snapshot.Definitions.FirstOrDefault(static definition =>
            string.Equals(definition.Key, "displayName", StringComparison.OrdinalIgnoreCase));

        if (displayNameDefinition is null)
        {
            return "Use this tool when you need the current user's profile data for personalization.";
        }

        var aliases = displayNameDefinition.Aliases.Count == 0
            ? "displayName"
            : $"displayName (aliases: {string.Join(", ", displayNameDefinition.Aliases)})";

        return $"Use this tool when you need the current user's display name or preferred user name for greeting the user, addressing them personally, or other personalization. Name field: {aliases}.";
    }
}
