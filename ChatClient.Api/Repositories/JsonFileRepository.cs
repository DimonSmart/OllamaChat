using ChatClient.Api.Services;
using System.Text.Json;

namespace ChatClient.Api.Repositories;

public class JsonFileRepository<T>
{
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public JsonFileRepository(string filePath, ILogger logger)
    {
        _filePath = Path.GetFullPath(filePath);
        _logger = logger;
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public bool Exists => File.Exists(_filePath);

    public async Task<T?> ReadAsync(CancellationToken cancellationToken = default)
    {
        return await SemaphoreHelper.ExecuteWithSemaphoreAsync(
            _semaphore,
            () => ReadInternalAsync(cancellationToken),
            _logger,
            $"Error reading {_filePath}");
    }

    public async Task WriteAsync(T data, CancellationToken cancellationToken = default)
    {
        await SemaphoreHelper.ExecuteWithSemaphoreAsync(
            _semaphore,
            () => WriteInternalAsync(data, cancellationToken),
            _logger,
            $"Error writing {_filePath}");
    }

    public async Task UpdateAsync(Func<T, Task> update, T defaultValue, CancellationToken cancellationToken = default)
    {
        await SemaphoreHelper.ExecuteWithSemaphoreAsync(
            _semaphore,
            async () =>
            {
                var data = await ReadInternalAsync(cancellationToken) ?? defaultValue;
                await update(data);
                await WriteInternalAsync(data, cancellationToken);
            },
            _logger,
            $"Error updating {_filePath}");
    }

    private async Task<T?> ReadInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
            return default;

        var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
        return JsonSerializer.Deserialize<T>(json);
    }

    private async Task WriteInternalAsync(T data, CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(data, options);
        await File.WriteAllTextAsync(_filePath, json, cancellationToken);
    }
}

