using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json;

namespace ChatClient.Api.Services.BuiltIn;

/// <summary>A single local string dictionary shared by the UI and the built-in MCP process.</summary>
internal sealed class UserProfileStore
{
    private const string FilePathConfigurationKey = "UserProfilePrefs:FilePath";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static UserProfileStore Default { get; } = new(ResolvePath());

    public UserProfileStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public Task<Dictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default) =>
        LockedAsync(() => ReadUnsafeAsync(cancellationToken), cancellationToken);

    public async Task<(bool Exists, string? Value)> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);
        var values = await GetAllAsync(cancellationToken);
        return values.TryGetValue(normalizedKey, out var value)
            ? (true, value)
            : (false, null);
    }

    public Task<bool> SaveAsync(
        IEnumerable<KeyValuePair<string, string>> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var normalized = Normalize(values, rejectDuplicates: true);
        return LockedAsync(async () =>
        {
            // A user editing an old file must not bypass its one-time backup.
            await ReadUnsafeAsync(cancellationToken);
            await WriteUnsafeAsync(normalized, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task<ProfileSetResult> SetAsync(
        string key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = NormalizeKey(key);
        var normalizedValue = value?.Trim();

        return LockedAsync(async () =>
        {
            var values = await ReadUnsafeAsync(cancellationToken);
            var storedKey = values.Keys.FirstOrDefault(existing =>
                string.Equals(existing, normalizedKey, StringComparison.OrdinalIgnoreCase)) ?? normalizedKey;

            if (string.IsNullOrEmpty(normalizedValue))
            {
                if (values.Remove(storedKey))
                    await WriteUnsafeAsync(values, cancellationToken);
                return new ProfileSetResult(storedKey, null, true);
            }

            if (!values.TryGetValue(storedKey, out var previous) ||
                !string.Equals(previous, normalizedValue, StringComparison.Ordinal))
            {
                values[storedKey] = normalizedValue;
                await WriteUnsafeAsync(values, cancellationToken);
            }

            return new ProfileSetResult(storedKey, normalizedValue, false);
        }, cancellationToken);
    }

    internal static Dictionary<string, string> Parse(string json, out bool needsMigration)
    {
        using var parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("User profile must be a JSON object.");

        var root = parsed.RootElement;
        needsMigration = root.TryGetProperty("values", out var stored) &&
                         stored.ValueKind == JsonValueKind.Object;

        return NormalizeJson(needsMigration ? stored : root);
    }

    private static Dictionary<string, string> NormalizeJson(JsonElement json)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in json.EnumerateObject())
        {
            var key = property.Name.Trim();
            if (key.Length == 0)
                continue;

            string? value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.GetRawText(),
                _ => null
            };
            value = value?.Trim();
            if (!string.IsNullOrEmpty(value))
                values[key] = value;
        }

        return values;
    }

    private async Task<Dictionary<string, string>> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var json = await File.ReadAllTextAsync(FilePath, cancellationToken);
        var values = Parse(json, out var needsMigration);
        if (needsMigration)
        {
            var backupPath = FilePath + ".legacy-" + Guid.NewGuid().ToString("N") + ".bak";
            File.Copy(FilePath, backupPath);
            await WriteUnsafeAsync(values, cancellationToken);
        }

        return values;
    }

    private static Dictionary<string, string> Normalize(
        IEnumerable<KeyValuePair<string, string>> source,
        bool rejectDuplicates)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawKey, rawValue) in source)
        {
            var key = NormalizeKey(rawKey);
            var value = rawValue?.Trim();
            if (string.IsNullOrEmpty(value))
                continue;

            if (rejectDuplicates && !normalized.TryAdd(key, value))
                throw new InvalidOperationException($"Duplicate user profile key '{key}'.");

            normalized[key] = value;
        }

        return normalized;
    }

    private async Task WriteUnsafeAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);

        // Stable order, no wrappers or implicit defaults.
        var ordered = values
            .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Key, StringComparer.Ordinal)
            .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.OrdinalIgnoreCase);
        var json = JsonSerializer.Serialize(ordered, JsonOptions);
        var tempPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(json.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }

            File.Move(tempPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private async Task<T> LockedAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            var lockPath = FilePath + ".lock";
            await using var fileLock = await AcquireLockAsync(lockPath, cancellationToken);
            return await action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<FileStream> AcquireLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private static string NormalizeKey(string? key)
    {
        var normalized = key?.Trim();
        if (string.IsNullOrEmpty(normalized))
            throw new ArgumentException("User profile key must not be empty.", nameof(key));
        return normalized;
    }

    private static string ResolvePath()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        return StoragePathResolver.ResolveUserPath(
            configuration,
            configuration[FilePathConfigurationKey],
            FilePathConstants.DefaultUserProfilePrefsFile);
    }
}

internal sealed record ProfileSetResult(string Key, string? Value, bool Deleted);
