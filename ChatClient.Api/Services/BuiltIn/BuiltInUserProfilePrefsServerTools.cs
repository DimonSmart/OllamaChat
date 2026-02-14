using System.ComponentModel;
using System.Text.Json;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ChatClient.Api.Services.BuiltIn;

[McpServerToolType]
public sealed class BuiltInUserProfilePrefsServerTools
{
    public static IBuiltInMcpServerDescriptor Descriptor { get; } = new BuiltInMcpServerDescriptor(
        id: Guid.Parse("c8c4a3cf-e2d5-4f4d-9a6f-4504e322a2b3"),
        key: "built-in-user-profile-prefs",
        name: "Built-in User Profile Prefs MCP Server",
        description: "Stores and retrieves user profile preferences (including preferred name via displayName/name/preferred_name), with validation and elicitation for missing values.",
        registerTools: static builder => builder.WithTools<BuiltInUserProfilePrefsServerTools>());

    private const int MaxElicitationAttempts = 3;
    private const string ValueFieldName = "value";
    private const string StoredSource = "stored";
    private const string ElicitedSource = "elicited";

    private sealed record PreferenceSpec(
        string Prompt,
        string? DefaultValue = null,
        IReadOnlyList<string>? AllowedValues = null,
        string? Description = null);

    private static readonly IReadOnlyDictionary<string, PreferenceSpec> _knownSpecs =
        new Dictionary<string, PreferenceSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["displayName"] = new(
                Prompt: "How should I address you?",
                Description: "Preferred user name used for personalized addressing."),
            ["preferredLanguage"] = new(
                Prompt: "Which language should I use by default when replying?",
                DefaultValue: "ru",
                AllowedValues: ["ru", "en", "es"],
                Description: "Default answer language."),
            ["tone"] = new(
                Prompt: "What communication tone do you prefer?",
                DefaultValue: "neutral",
                AllowedValues: ["neutral", "friendly", "formal"]),
            ["verbosity"] = new(
                Prompt: "How detailed should responses be?",
                DefaultValue: "normal",
                AllowedValues: ["short", "normal", "detailed"]),
            ["timezone"] = new(
                Prompt: "Which time zone should be used for time-related information?",
                DefaultValue: "Europe/Madrid"),
            ["measurementSystem"] = new(
                Prompt: "Which measurement system should be used?",
                DefaultValue: "metric",
                AllowedValues: ["metric", "imperial"]),
            ["grammarGenderRu"] = new(
                Prompt: "Which grammatical gender forms should be used in Russian?",
                DefaultValue: "neutral",
                AllowedValues: ["male", "female", "neutral"]),
            ["signature"] = new("What signature should be used in messages?"),
            ["devEnvironment"] = new(
                Prompt: "What operating system do you use?",
                DefaultValue: "windows",
                AllowedValues: ["windows", "macos", "linux", "other"]),
            ["editor"] = new(
                Prompt: "Which IDE or editor do you use?",
                DefaultValue: "vscode",
                AllowedValues: ["vs", "vscode", "rider", "other"])
        };

    private static readonly IReadOnlyDictionary<string, string> _canonicalKeyByAlias =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = "displayName",
            ["preferred_name"] = "displayName",
            ["preferredName"] = "displayName",
            ["userName"] = "displayName"
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _legacyAliasesByCanonicalKey =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["displayName"] = ["name", "preferred_name", "preferredName", "userName"]
        };

    [McpServerTool(Name = "prefs_get"), Description("Gets one user preference by key. Accepts aliases (displayName, name, preferred_name). If missing, asks user via elicitation, validates, saves, and returns it.")]
    public static async Task<object> PrefsGetAsync(
        McpServer server,
        [Description("Preference key. Name key aliases: displayName, name, preferred_name, preferredName, userName. Other known keys: preferredLanguage, tone, verbosity, timezone, measurementSystem, grammarGenderRu, signature, devEnvironment, editor.")] string key,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);
        var storedValues = await UserProfilePrefsFileStore.GetAllAsync(cancellationToken);

        if (TryGetStoredValue(storedValues, normalizedKey, out var storedValue) &&
            TryNormalizeValue(normalizedKey, storedValue, out var normalizedStoredValue))
        {
            if (!storedValues.ContainsKey(normalizedKey))
            {
                await UserProfilePrefsFileStore.SetAsync(normalizedKey, normalizedStoredValue, cancellationToken);
            }

            return new
            {
                key = normalizedKey,
                exists = true,
                value = normalizedStoredValue,
                source = StoredSource
            };
        }

        var spec = GetSpecOrFallback(normalizedKey);
        var elicitedValue = await ElicitPreferenceValueAsync(server, normalizedKey, spec, cancellationToken);
        await UserProfilePrefsFileStore.SetAsync(normalizedKey, elicitedValue, cancellationToken);

        return new
        {
            key = normalizedKey,
            exists = true,
            value = elicitedValue,
            source = ElicitedSource
        };
    }

    [McpServerTool(Name = "prefs_get_all"), Description("Returns all stored user preferences normalized to canonical keys. Includes known keys and accepted aliases (for name: displayName, name, preferred_name).")]
    public static async Task<object> PrefsGetAllAsync(CancellationToken cancellationToken = default)
    {
        var storedValues = await UserProfilePrefsFileStore.GetAllAsync(cancellationToken);
        var normalizedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var knownKeys = _knownSpecs.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        var acceptedAliases = _canonicalKeyByAlias.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray();

        foreach (var (storedKey, rawValue) in storedValues)
        {
            var normalizedKey = NormalizeKey(storedKey);
            if (TryNormalizeValue(normalizedKey, rawValue, out var normalizedValue))
            {
                normalizedValues[normalizedKey] = normalizedValue;
            }
        }

        return new
        {
            values = normalizedValues,
            knownKeys,
            acceptedAliases,
            supportedKeys = knownKeys
        };
    }

    [McpServerTool(Name = "prefs_reset_all"), Description("Clears all stored user preferences. If confirm is false, asks the user for confirmation first.")]
    public static async Task<object> PrefsResetAllAsync(
        McpServer server,
        [Description("When true, reset happens without additional user confirmation.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        var shouldReset = confirm || await ConfirmResetAsync(server, cancellationToken);
        if (!shouldReset)
        {
            return new
            {
                cleared = false
            };
        }

        await UserProfilePrefsFileStore.ClearAsync(cancellationToken);
        return new
        {
            cleared = true
        };
    }

    private static PreferenceSpec GetSpecOrFallback(string key)
    {
        if (_knownSpecs.TryGetValue(key, out var spec))
            return spec;

        return new PreferenceSpec(Prompt: $"Enter a value for preference '{key}'.");
    }

    private static bool TryGetStoredValue(
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

        if (_legacyAliasesByCanonicalKey.TryGetValue(normalizedKey, out var aliases))
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

    private static async Task<string> ElicitPreferenceValueAsync(
        McpServer server,
        string key,
        PreferenceSpec spec,
        CancellationToken cancellationToken)
    {
        string? validationMessage = null;

        for (var attempt = 0; attempt < MaxElicitationAttempts; attempt++)
        {
            var request = BuildPreferenceElicitationRequest(key, spec, validationMessage);
            var response = await server.ElicitAsync(request, cancellationToken);

            if (!response.IsAccepted)
            {
                throw new InvalidOperationException("user_canceled");
            }

            if (TryReadContentValue(response, ValueFieldName, out var rawValue) &&
                TryNormalizeValue(key, rawValue, out var normalizedValue))
            {
                return normalizedValue;
            }

            validationMessage = spec.AllowedValues is { Count: > 0 }
                ? "Choose one of the suggested values."
                : "Value must not be empty.";
        }

        throw new InvalidOperationException("invalid_value");
    }

    private static ElicitRequestParams BuildPreferenceElicitationRequest(
        string key,
        PreferenceSpec spec,
        string? validationMessage)
    {
        var message = string.IsNullOrWhiteSpace(validationMessage)
            ? spec.Prompt
            : $"{validationMessage} {spec.Prompt}";

        return new ElicitRequestParams
        {
            Mode = "form",
            Message = message,
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
                {
                    [ValueFieldName] = BuildPreferenceSchema(key, spec)
                },
                Required = [ValueFieldName]
            }
        };
    }

    private static ElicitRequestParams.PrimitiveSchemaDefinition BuildPreferenceSchema(string key, PreferenceSpec spec)
    {
        if (spec.AllowedValues is { Count: > 0 })
        {
            return new ElicitRequestParams.TitledSingleSelectEnumSchema
            {
                Type = "string",
                Title = key,
                Description = spec.Description,
                OneOf = spec.AllowedValues
                    .Select(static value => new ElicitRequestParams.EnumSchemaOption
                    {
                        Const = value,
                        Title = value
                    })
                    .ToArray(),
                Default = spec.DefaultValue
            };
        }

        return new ElicitRequestParams.StringSchema
        {
            Type = "string",
            Title = key,
            Description = spec.Description,
            Default = spec.DefaultValue
        };
    }

    private static bool TryNormalizeValue(string key, string? rawValue, out string normalizedValue)
    {
        normalizedValue = string.Empty;
        if (rawValue is null)
            return false;

        var trimmed = rawValue.Trim();
        if (trimmed.Length == 0)
            return false;

        if (_knownSpecs.TryGetValue(key, out var spec) && spec.AllowedValues is { Count: > 0 })
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

    private static async Task<bool> ConfirmResetAsync(McpServer server, CancellationToken cancellationToken)
    {
        var request = new ElicitRequestParams
        {
            Mode = "form",
            Message = "Confirm resetting all saved preferences?",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
                {
                    ["confirm"] = new ElicitRequestParams.TitledSingleSelectEnumSchema
                    {
                        Type = "string",
                        Title = "Confirmation",
                        OneOf =
                        [
                            new ElicitRequestParams.EnumSchemaOption { Const = "yes", Title = "Yes" },
                            new ElicitRequestParams.EnumSchemaOption { Const = "no", Title = "No" }
                        ],
                        Default = "no"
                    }
                },
                Required = ["confirm"]
            }
        };

        var response = await server.ElicitAsync(request, cancellationToken);
        if (!response.IsAccepted)
            return false;

        if (!TryReadContentValue(response, "confirm", out var decision))
            return false;

        return string.Equals(decision, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadContentValue(ElicitResult response, string key, out string value)
    {
        value = string.Empty;
        if (response.Content is null || !response.Content.TryGetValue(key, out var jsonValue))
            return false;

        value = jsonValue.ValueKind switch
        {
            JsonValueKind.String => jsonValue.GetString() ?? string.Empty,
            JsonValueKind.Number => jsonValue.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => jsonValue.GetRawText()
        };

        return true;
    }

    private static string NormalizeKey(string? key)
    {
        var normalizedKey = key?.Trim() ?? string.Empty;
        if (normalizedKey.Length == 0)
        {
            throw new InvalidOperationException("empty_key");
        }

        return _canonicalKeyByAlias.TryGetValue(normalizedKey, out var canonicalKey)
            ? canonicalKey
            : normalizedKey;
    }
}

internal static class UserProfilePrefsFileStore
{
    private const string UserProfileFilePathConfigKey = "UserProfilePrefs:FilePath";

    private static readonly SemaphoreSlim _ioLock = new(1, 1);
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly string _filePath = ResolvePath();

    public static async Task<Dictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            return await ReadUnsafeAsync(cancellationToken);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public static async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            var values = await ReadUnsafeAsync(cancellationToken);
            values[key.Trim()] = value;
            await WriteUnsafeAsync(values, cancellationToken);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public static async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            await WriteUnsafeAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), cancellationToken);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private static async Task<Dictionary<string, string>> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        await using var fileStream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fileStream.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var values = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(fileStream, _jsonOptions, ct);
        return values is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task WriteUnsafeAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var ordered = values
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key))
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        var json = JsonSerializer.Serialize(ordered, _jsonOptions);
        await File.WriteAllTextAsync(_filePath, json, cancellationToken);
    }

    private static string ResolvePath()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        return StoragePathResolver.ResolveUserPath(
            configuration,
            configuration[UserProfileFilePathConfigKey],
            FilePathConstants.DefaultUserProfilePrefsFile);
    }
}
