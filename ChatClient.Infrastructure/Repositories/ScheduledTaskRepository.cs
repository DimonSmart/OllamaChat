using System.Text.Json;
using System.Text.Json.Serialization;
using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public sealed class ScheduledTaskRepository : IScheduledTaskRepository
{
    private readonly JsonFileRepository<List<ScheduledTaskDefinition>> _repo;

    public ScheduledTaskRepository(IConfiguration configuration, ILogger<ScheduledTaskRepository> logger)
    {
        var path = StoragePathResolver.ResolveUserPath(configuration,
            configuration["ScheduledTasks:FilePath"], FilePathConstants.DefaultScheduledTasksFile);
        var json = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        _repo = new JsonFileRepository<List<ScheduledTaskDefinition>>(
            path, logger, json, atomicWrites: true, rejectNullRead: true);
    }

    public async Task<IReadOnlyList<ScheduledTaskDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var items = await _repo.ReadAsync(cancellationToken) ?? [];
        VerifyStructure(items);
        return items;
    }

    public async Task<TResult> UpdateAsync<TResult>(
        Func<List<ScheduledTaskDefinition>, TResult> mutation, CancellationToken cancellationToken = default)
    {
        TResult result = default!;
        await _repo.UpdateAsync(items =>
        {
            VerifyStructure(items);
            result = mutation(items);
            return Task.CompletedTask;
        }, [], cancellationToken);
        return result;
    }

    private static void VerifyStructure(List<ScheduledTaskDefinition> items)
    {
        if (items.Any(static item => item is null))
            throw new InvalidDataException("Scheduled tasks JSON contains a null record.");
        if (items.GroupBy(static item => item.Id).Any(static group => group.Count() > 1))
            throw new InvalidDataException("Scheduled tasks JSON contains duplicate timer IDs.");
    }
}
