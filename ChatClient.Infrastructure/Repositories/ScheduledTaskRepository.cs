using System.Text.Json;
using System.Text.Json.Serialization;
using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

/// <summary>Single-process, read/modify/atomic-replace persistence with one lock for the entire operation.</summary>
public sealed class ScheduledTaskRepository : IScheduledTaskRepository
{
    private readonly string _path;
    private readonly ILogger<ScheduledTaskRepository> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ScheduledTaskRepository(IConfiguration configuration, ILogger<ScheduledTaskRepository> logger)
    {
        _path = StoragePathResolver.ResolveUserPath(configuration,
            configuration["ScheduledTasks:FilePath"], FilePathConstants.DefaultScheduledTasksFile);
        _logger = logger;
    }

    public async Task<IReadOnlyList<ScheduledTaskDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadCoreAsync(cancellationToken); }
        catch (Exception e)
        {
            _logger.LogError(e, "Cannot read scheduled tasks from {Path}", _path);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<TResult> UpdateAsync<TResult>(
        Func<List<ScheduledTaskDefinition>, TResult> mutation, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = await ReadCoreAsync(cancellationToken);
            var result = mutation(items);
            var contents = JsonSerializer.Serialize(items, _json);
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, "." + Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllTextAsync(temporary, contents, cancellationToken);
                // On a single volume File.Move with overwrite replaces the destination atomically.
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            return result;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Cannot update scheduled tasks in {Path}", _path);
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task<List<ScheduledTaskDefinition>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return [];
        var contents = await File.ReadAllTextAsync(_path, cancellationToken);
        using var document = JsonDocument.Parse(contents);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Scheduled tasks JSON root must be an array. The file has not been overwritten.");
        var result = JsonSerializer.Deserialize<List<ScheduledTaskDefinition>>(contents, _json);
        if (result is null || result.Any(static item => item is null))
            throw new InvalidDataException("Scheduled tasks JSON contains an invalid record.");
        return result;
    }
}
