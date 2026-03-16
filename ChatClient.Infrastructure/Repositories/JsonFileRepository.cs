using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace ChatClient.Infrastructure.Repositories;

public class JsonFileRepository<T>
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new();

    private readonly string _filePath;
    private readonly string _lockKey;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _options;

    public JsonFileRepository(string filePath, ILogger logger, JsonSerializerOptions? options = null)
    {
        _filePath = Path.GetFullPath(filePath);
        _lockKey = OperatingSystem.IsWindows() ? _filePath.ToUpperInvariant() : _filePath;
        _logger = logger;
        _options = options ?? new JsonSerializerOptions { WriteIndented = true };
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public bool Exists => File.Exists(_filePath);

    public Task<T?> ReadAsync(CancellationToken cancellationToken = default) =>
        ExecuteWithFileLockAsync(
            () => ReadInternalAsync(cancellationToken),
            $"Error reading {_filePath}",
            cancellationToken);

    public Task WriteAsync(T data, CancellationToken cancellationToken = default) =>
        ExecuteWithFileLockAsync(
            () => WriteInternalAsync(data, cancellationToken),
            $"Error writing {_filePath}",
            cancellationToken);

    public Task DeleteAsync(CancellationToken cancellationToken = default) =>
        ExecuteWithFileLockAsync(
            () =>
            {
                if (File.Exists(_filePath))
                    File.Delete(_filePath);

                return Task.CompletedTask;
            },
            $"Error deleting {_filePath}",
            cancellationToken);

    public Task UpdateAsync(Func<T, Task> update, T defaultValue, CancellationToken cancellationToken = default) =>
        ExecuteWithFileLockAsync(
            async () =>
            {
                var data = await ReadInternalAsync(cancellationToken) ?? defaultValue;
                await update(data);
                await WriteInternalAsync(data, cancellationToken);
            },
            $"Error updating {_filePath}",
            cancellationToken);

    private async Task<T?> ReadInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
            return default;

        var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
        return JsonSerializer.Deserialize<T>(json, _options);
    }

    private async Task WriteInternalAsync(T data, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(data, _options);
        await File.WriteAllTextAsync(_filePath, json, cancellationToken);
    }

    private async Task<TValue> ExecuteWithFileLockAsync<TValue>(
        Func<Task<TValue>> operation,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var fileLock = FileLocks.GetOrAdd(_lockKey, static _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, errorMessage);
            throw;
        }
        finally
        {
            fileLock.Release();
        }
    }

    private async Task ExecuteWithFileLockAsync(
        Func<Task> operation,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await ExecuteWithFileLockAsync(
            async () =>
            {
                await operation();
                return true;
            },
            errorMessage,
            cancellationToken);
    }
}
