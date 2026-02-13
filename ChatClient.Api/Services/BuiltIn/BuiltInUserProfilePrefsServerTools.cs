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
    private const int MaxElicitationAttempts = 3;
    private const string ValueFieldName = "value";
    private const string StoredSource = "stored";
    private const string ElicitedSource = "elicited";

    private static readonly PreferenceDefinition[] _supportedPreferences =
    [
        new(
            Key: "displayName",
            Title: "Display name",
            Description: "Как к вам обращаться.",
            Question: "Как к вам обращаться?",
            Kind: PreferenceValueKind.String,
            DefaultValue: null,
            Options: []),
        new(
            Key: "preferredLanguage",
            Title: "Preferred language",
            Description: "Язык ответов по умолчанию.",
            Question: "На каком языке отвечать по умолчанию?",
            Kind: PreferenceValueKind.Enum,
            DefaultValue: "ru",
            Options:
            [
                new("ru", "Русский"),
                new("en", "English"),
                new("es", "Español")
            ]),
        new(
            Key: "tone",
            Title: "Tone",
            Description: "Тон общения.",
            Question: "Какой тон общения предпочитаете?",
            Kind: PreferenceValueKind.Enum,
            DefaultValue: "neutral",
            Options:
            [
                new("neutral", "Neutral"),
                new("friendly", "Friendly"),
                new("formal", "Formal")
            ]),
        new(
            Key: "verbosity",
            Title: "Verbosity",
            Description: "Насколько подробными должны быть ответы.",
            Question: "Насколько подробные ответы вам удобнее?",
            Kind: PreferenceValueKind.Enum,
            DefaultValue: "normal",
            Options:
            [
                new("short", "Short"),
                new("normal", "Normal"),
                new("detailed", "Detailed")
            ]),
        new(
            Key: "timezone",
            Title: "Time zone",
            Description: "Часовой пояс для времени и дат.",
            Question: "Какой часовой пояс использовать для времени?",
            Kind: PreferenceValueKind.String,
            DefaultValue: "Europe/Madrid",
            Options: []),
        new(
            Key: "measurementSystem",
            Title: "Measurement system",
            Description: "Система единиц измерения.",
            Question: "Какие единицы измерения использовать?",
            Kind: PreferenceValueKind.Enum,
            DefaultValue: "metric",
            Options:
            [
                new("metric", "Metric"),
                new("imperial", "Imperial")
            ]),
        new(
            Key: "grammarGenderRu",
            Title: "Russian grammar gender",
            Description: "Предпочтительные формы рода в русском языке.",
            Question: "Какие формы в русском использовать?",
            Kind: PreferenceValueKind.Enum,
            DefaultValue: "neutral",
            Options:
            [
                new("male", "Male"),
                new("female", "Female"),
                new("neutral", "Neutral")
            ]),
        new(
            Key: "signature",
            Title: "Signature",
            Description: "Подпись для писем или сообщений.",
            Question: "Какую подпись использовать в письмах/сообщениях?",
            Kind: PreferenceValueKind.String,
            DefaultValue: null,
            Options: []),
        new(
            Key: "devEnvironment",
            Title: "Development environment",
            Description: "Основная ОС для команд и инструкций.",
            Question: "Какая у вас ОС?",
            Kind: PreferenceValueKind.Enum,
            DefaultValue: "windows",
            Options:
            [
                new("windows", "Windows"),
                new("macos", "macOS"),
                new("linux", "Linux"),
                new("other", "Other")
            ]),
        new(
            Key: "editor",
            Title: "Editor",
            Description: "Основная IDE или редактор.",
            Question: "Какая IDE/редактор?",
            Kind: PreferenceValueKind.Enum,
            DefaultValue: "vscode",
            Options:
            [
                new("vs", "Visual Studio"),
                new("vscode", "VS Code"),
                new("rider", "Rider"),
                new("other", "Other")
            ])
    ];

    private static readonly IReadOnlyDictionary<string, PreferenceDefinition> _supportedByKey =
        _supportedPreferences.ToDictionary(static preference => preference.Key, static preference => preference, StringComparer.OrdinalIgnoreCase);

    [McpServerTool(Name = "prefs_get"), Description("Gets one user preference by key. If missing, asks user via elicitation, validates, saves, and returns it.")]
    public static async Task<object> PrefsGetAsync(
        McpServer server,
        [Description("Supported key: displayName, preferredLanguage, tone, verbosity, timezone, measurementSystem, grammarGenderRu, signature, devEnvironment, editor.")] string key,
        CancellationToken cancellationToken = default)
    {
        var preference = GetSupportedPreferenceOrThrow(key);
        var values = await UserProfilePrefsFileStore.GetAllAsync(cancellationToken);
        if (values.TryGetValue(preference.Key, out var storedValue) &&
            TryNormalizeValue(preference, storedValue, out var normalizedStoredValue))
        {
            return new
            {
                key = preference.Key,
                exists = true,
                value = normalizedStoredValue,
                source = StoredSource
            };
        }

        var elicitedValue = await ElicitPreferenceValueAsync(server, preference, cancellationToken);
        await UserProfilePrefsFileStore.SetAsync(preference.Key, elicitedValue, cancellationToken);

        return new
        {
            key = preference.Key,
            exists = true,
            value = elicitedValue,
            source = ElicitedSource
        };
    }

    [McpServerTool(Name = "prefs_get_all"), Description("Returns all stored user preferences and the full supported key list.")]
    public static async Task<object> PrefsGetAllAsync(CancellationToken cancellationToken = default)
    {
        var storedValues = await UserProfilePrefsFileStore.GetAllAsync(cancellationToken);
        var normalizedValues = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var preference in _supportedPreferences)
        {
            if (!storedValues.TryGetValue(preference.Key, out var rawValue))
                continue;

            if (TryNormalizeValue(preference, rawValue, out var normalizedValue))
            {
                normalizedValues[preference.Key] = normalizedValue;
            }
        }

        return new
        {
            values = normalizedValues,
            supportedKeys = _supportedPreferences.Select(static preference => preference.Key).ToArray()
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

    private static PreferenceDefinition GetSupportedPreferenceOrThrow(string? key)
    {
        var normalizedKey = key?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedKey) ||
            !_supportedByKey.TryGetValue(normalizedKey, out var preference))
        {
            throw new InvalidOperationException("unknown_key");
        }

        return preference;
    }

    private static async Task<string> ElicitPreferenceValueAsync(
        McpServer server,
        PreferenceDefinition preference,
        CancellationToken cancellationToken)
    {
        string? validationMessage = null;

        for (var attempt = 0; attempt < MaxElicitationAttempts; attempt++)
        {
            var request = BuildPreferenceElicitationRequest(preference, validationMessage);
            var response = await server.ElicitAsync(request, cancellationToken);

            if (!response.IsAccepted)
            {
                throw new InvalidOperationException("user_canceled");
            }

            if (TryReadContentValue(response, ValueFieldName, out var rawValue) &&
                TryNormalizeValue(preference, rawValue, out var normalizedValue))
            {
                return normalizedValue;
            }

            validationMessage = preference.Kind == PreferenceValueKind.Enum
                ? "Выберите одно из предложенных значений."
                : "Значение не должно быть пустым.";
        }

        throw new InvalidOperationException("invalid_value");
    }

    private static ElicitRequestParams BuildPreferenceElicitationRequest(
        PreferenceDefinition preference,
        string? validationMessage)
    {
        var message = string.IsNullOrWhiteSpace(validationMessage)
            ? preference.Question
            : $"{validationMessage} {preference.Question}";

        return new ElicitRequestParams
        {
            Mode = "form",
            Message = message,
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
                {
                    [ValueFieldName] = BuildPreferenceSchema(preference)
                },
                Required = [ValueFieldName]
            }
        };
    }

    private static ElicitRequestParams.PrimitiveSchemaDefinition BuildPreferenceSchema(PreferenceDefinition preference)
    {
        if (preference.Kind == PreferenceValueKind.Enum)
        {
            return new ElicitRequestParams.TitledSingleSelectEnumSchema
            {
                Type = "string",
                Title = preference.Title,
                Description = preference.Description,
                OneOf = preference.Options
                    .Select(static option => new ElicitRequestParams.EnumSchemaOption
                    {
                        Const = option.Value,
                        Title = option.Label
                    })
                    .ToArray(),
                Default = preference.DefaultValue
            };
        }

        return new ElicitRequestParams.StringSchema
        {
            Type = "string",
            Title = preference.Title,
            Description = preference.Description,
            Default = preference.DefaultValue
        };
    }

    private static bool TryNormalizeValue(
        PreferenceDefinition preference,
        string? rawValue,
        out string normalizedValue)
    {
        normalizedValue = string.Empty;
        if (rawValue is null)
            return false;

        var trimmed = rawValue.Trim();
        if (trimmed.Length == 0)
            return false;

        if (preference.Kind != PreferenceValueKind.Enum)
        {
            normalizedValue = trimmed;
            return true;
        }

        foreach (var option in preference.Options)
        {
            if (!string.Equals(option.Value, trimmed, StringComparison.OrdinalIgnoreCase))
                continue;

            normalizedValue = option.Value;
            return true;
        }

        return false;
    }

    private static async Task<bool> ConfirmResetAsync(McpServer server, CancellationToken cancellationToken)
    {
        var request = new ElicitRequestParams
        {
            Mode = "form",
            Message = "Подтвердить сброс всех сохраненных настроек?",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
                {
                    ["confirm"] = new ElicitRequestParams.TitledSingleSelectEnumSchema
                    {
                        Type = "string",
                        Title = "Подтверждение",
                        Description = "Выберите действие.",
                        OneOf =
                        [
                            new ElicitRequestParams.EnumSchemaOption { Const = "yes", Title = "Да, сбросить" },
                            new ElicitRequestParams.EnumSchemaOption { Const = "no", Title = "Нет, оставить" }
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

        value = JsonElementToString(jsonValue);
        return true;
    }

    private static string JsonElementToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => value.GetRawText()
        };
    }

    private enum PreferenceValueKind
    {
        String,
        Enum
    }

    private sealed record PreferenceOption(string Value, string Label);

    private sealed record PreferenceDefinition(
        string Key,
        string Title,
        string Description,
        string Question,
        PreferenceValueKind Kind,
        string? DefaultValue,
        IReadOnlyList<PreferenceOption> Options);
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

        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            var values = await ReadUnsafeAsync(cancellationToken);
            values[key] = value;
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

    private static async Task<Dictionary<string, string>> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, _jsonOptions);
            if (parsed is null)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in parsed)
            {
                if (string.IsNullOrWhiteSpace(key) || value is null)
                    continue;

                normalized[key] = value;
            }

            return normalized;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
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
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        var json = JsonSerializer.Serialize(ordered, _jsonOptions);
        await File.WriteAllTextAsync(_filePath, json, cancellationToken);
    }

    private static string ResolvePath()
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var configurationBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true);

        if (!string.IsNullOrWhiteSpace(environmentName))
        {
            configurationBuilder.AddJsonFile($"appsettings.{environmentName}.json", optional: true);
        }

        configurationBuilder.AddEnvironmentVariables();
        var configuration = configurationBuilder.Build();

        return StoragePathResolver.ResolveUserPath(
            configuration,
            configuration[UserProfileFilePathConfigKey],
            FilePathConstants.DefaultUserProfilePrefsFile);
    }
}
