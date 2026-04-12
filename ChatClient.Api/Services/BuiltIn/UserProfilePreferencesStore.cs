using System.Text.Json;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;

namespace ChatClient.Api.Services.BuiltIn;

internal static class UserProfilePreferencesStore
{
    private const string UserProfileFilePathConfigKey = "UserProfilePrefs:FilePath";

    private static readonly SemaphoreSlim _ioLock = new(1, 1);
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly string _filePath = ResolvePath();
    private static UserProfilePreferencesDocument? _cachedDocument;

    public static string FilePath => _filePath;

    public static async Task<UserProfilePreferencesDocument> GetAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            return CloneDocument(ReadUnsafe(useDefaultWhenMissing: true));
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public static UserProfilePreferencesDocument GetSnapshot() =>
        CloneDocument(ReadThreadSafe(useDefaultWhenMissing: true));

    public static async Task SaveAsync(
        UserProfilePreferencesDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            var normalized = UserProfilePreferencesRuntime.NormalizeDocument(document, useDefaultWhenMissing: false);
            await WriteUnsafeAsync(normalized, cancellationToken);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public static async Task SetValueAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            var document = ReadUnsafe(useDefaultWhenMissing: true);
            var snapshot = UserProfilePreferencesRuntime.CreateSnapshot(document, useDefaultWhenMissing: true);
            if (!snapshot.TryResolveKey(key, out var normalizedKey))
            {
                throw new InvalidOperationException($"Preference key '{key}' is not configured.");
            }

            if (!snapshot.TryNormalizeValue(normalizedKey, value, out var normalizedValue))
            {
                throw new InvalidOperationException($"Preference value for '{normalizedKey}' is invalid.");
            }

            document.Values[normalizedKey] = normalizedValue;
            await WriteUnsafeAsync(UserProfilePreferencesRuntime.NormalizeDocument(document, useDefaultWhenMissing: false), cancellationToken);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public static async Task ClearValuesAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            var document = ReadUnsafe(useDefaultWhenMissing: true);
            document.Values.Clear();
            await WriteUnsafeAsync(UserProfilePreferencesRuntime.NormalizeDocument(document, useDefaultWhenMissing: false), cancellationToken);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private static UserProfilePreferencesDocument ReadThreadSafe(bool useDefaultWhenMissing)
    {
        _ioLock.Wait();
        try
        {
            return ReadUnsafe(useDefaultWhenMissing);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private static UserProfilePreferencesDocument ReadUnsafe(bool useDefaultWhenMissing)
    {
        if (_cachedDocument is not null)
        {
            return CloneDocument(_cachedDocument);
        }

        UserProfilePreferencesDocument? document = null;
        if (File.Exists(_filePath))
        {
            var json = File.ReadAllText(_filePath);
            document = UserProfilePreferencesDocumentParser.Deserialize(json, _jsonOptions, useDefaultWhenMissing);
        }

        _cachedDocument = document is null
            ? UserProfilePreferencesRuntime.NormalizeDocument(null, useDefaultWhenMissing)
            : UserProfilePreferencesRuntime.NormalizeDocument(document, useDefaultWhenMissing);
        return CloneDocument(_cachedDocument);
    }

    private static async Task WriteUnsafeAsync(
        UserProfilePreferencesDocument document,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _cachedDocument = CloneDocument(document);
        var json = JsonSerializer.Serialize(_cachedDocument, _jsonOptions);
        await File.WriteAllTextAsync(_filePath, json, cancellationToken);
    }

    private static UserProfilePreferencesDocument CloneDocument(UserProfilePreferencesDocument document) =>
        new()
        {
            ServerDescription = document.ServerDescription,
            Definitions = document.Definitions
                .Select(static definition => new UserProfilePreferenceDefinition
                {
                    Key = definition.Key,
                    Description = definition.Description,
                    Prompt = definition.Prompt,
                    DefaultValue = definition.DefaultValue,
                    AllowedValues = definition.AllowedValues.ToList(),
                    Aliases = definition.Aliases.ToList()
                })
                .ToList(),
            Values = new Dictionary<string, string>(document.Values, StringComparer.OrdinalIgnoreCase)
        };

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
