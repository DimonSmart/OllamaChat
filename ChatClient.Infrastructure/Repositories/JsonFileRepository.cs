using System.Text.Json;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public class JsonFileRepository<T>
{
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly JsonSerializerOptions _options;

    public JsonFileRepository(string filePath, ILogger logger, JsonSerializerOptions? options = null)
    {
        _filePath = Path.GetFullPath(filePath);
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
        SemaphoreHelper.ExecuteWithSemaphoreAsync(
            _semaphore,
            () => ReadInternalAsync(cancellationToken),
            _logger,
            $"Error reading {_filePath}");

    public Task WriteAsync(T data, CancellationToken cancellationToken = default) =>
        SemaphoreHelper.ExecuteWithSemaphoreAsync(
            _semaphore,
            () => WriteInternalAsync(data, cancellationToken),
            _logger,
            $"Error writing {_filePath}");

    public Task UpdateAsync(Func<T, Task> update, T defaultValue, CancellationToken cancellationToken = default) =>
        SemaphoreHelper.ExecuteWithSemaphoreAsync(
            _semaphore,
            async () =>
            {
                var data = await ReadInternalAsync(cancellationToken) ?? defaultValue;
                await update(data);
                await WriteInternalAsync(data, cancellationToken);
            },
            _logger,
            $"Error updating {_filePath}");

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
}
