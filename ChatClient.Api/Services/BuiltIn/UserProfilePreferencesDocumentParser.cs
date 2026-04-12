using System.Text;
using System.Text.Json;

namespace ChatClient.Api.Services.BuiltIn;

internal static class UserProfilePreferencesDocumentParser
{
    private static readonly string[] KnownDocumentProperties =
    [
        "serverDescription",
        "definitions",
        "values"
    ];

    public static UserProfilePreferencesDocument Deserialize(
        string? json,
        JsonSerializerOptions jsonOptions,
        bool useDefaultWhenMissing)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return UserProfilePreferencesRuntime.NormalizeDocument(null, useDefaultWhenMissing);
        }

        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (!root.EnumerateObject().Any())
            {
                return UserProfilePreferencesRuntime.NormalizeDocument(null, useDefaultWhenMissing);
            }

            if (IsLegacyFlatMap(root))
            {
                return UserProfilePreferencesRuntime.NormalizeDocument(
                    CreateMigratedLegacyDocument(root),
                    useDefaultWhenMissing);
            }
        }

        var document = JsonSerializer.Deserialize<UserProfilePreferencesDocument>(json, jsonOptions);
        return UserProfilePreferencesRuntime.NormalizeDocument(document, useDefaultWhenMissing);
    }

    private static bool IsLegacyFlatMap(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (KnownDocumentProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static UserProfilePreferencesDocument CreateMigratedLegacyDocument(JsonElement root)
    {
        var document = UserProfilePreferencesDocument.CreateDefault();
        var snapshot = UserProfilePreferencesRuntime.CreateSnapshot(document, useDefaultWhenMissing: true);

        foreach (var property in root.EnumerateObject())
        {
            var key = property.Name.Trim();
            if (string.IsNullOrWhiteSpace(key) || !TryReadScalarValue(property.Value, out var rawValue))
            {
                continue;
            }

            if (!snapshot.TryResolveKey(key, out var normalizedKey))
            {
                var migratedDefinition = CreateMigratedDefinition(key);
                document.Definitions.Add(migratedDefinition);
                snapshot = UserProfilePreferencesRuntime.CreateSnapshot(document, useDefaultWhenMissing: true);
                normalizedKey = migratedDefinition.Key;
            }

            if (!snapshot.TryNormalizeValue(normalizedKey, rawValue, out var normalizedValue))
            {
                if (!TryExtendAllowedValues(document, normalizedKey, rawValue))
                {
                    continue;
                }

                snapshot = UserProfilePreferencesRuntime.CreateSnapshot(document, useDefaultWhenMissing: true);
                if (!snapshot.TryNormalizeValue(normalizedKey, rawValue, out normalizedValue))
                {
                    continue;
                }
            }

            document.Values[normalizedKey] = normalizedValue;
        }

        return document;
    }

    private static UserProfilePreferenceDefinition CreateMigratedDefinition(string key)
    {
        var promptLabel = BuildPromptLabel(key);
        return new UserProfilePreferenceDefinition
        {
            Key = key,
            Description = $"User-specific preference stored under key '{key}'.",
            Prompt = $"What should I use for {promptLabel}?"
        };
    }

    private static bool TryExtendAllowedValues(
        UserProfilePreferencesDocument document,
        string key,
        string rawValue)
    {
        var trimmedValue = rawValue.Trim();
        if (trimmedValue.Length == 0)
        {
            return false;
        }

        var definition = document.Definitions.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
        if (definition is null || definition.AllowedValues.Count == 0)
        {
            return false;
        }

        if (definition.AllowedValues.Contains(trimmedValue, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        definition.AllowedValues.Add(trimmedValue);
        return true;
    }

    private static bool TryReadScalarValue(JsonElement value, out string rawValue)
    {
        rawValue = string.Empty;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                rawValue = value.GetString() ?? string.Empty;
                return true;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                rawValue = value.GetRawText();
                return true;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return false;
            default:
                return false;
        }
    }

    private static string BuildPromptLabel(string key)
    {
        StringBuilder builder = new();
        var previousWasSeparator = true;

        foreach (var character in key)
        {
            if (character is '_' or '-' or '.')
            {
                if (!previousWasSeparator)
                {
                    builder.Append(' ');
                    previousWasSeparator = true;
                }

                continue;
            }

            if (builder.Length > 0 &&
                char.IsUpper(character) &&
                !previousWasSeparator &&
                char.IsLetterOrDigit(builder[^1]) &&
                !char.IsUpper(builder[^1]))
            {
                builder.Append(' ');
            }

            builder.Append(char.ToLowerInvariant(character));
            previousWasSeparator = false;
        }

        return builder.ToString().Trim();
    }
}
