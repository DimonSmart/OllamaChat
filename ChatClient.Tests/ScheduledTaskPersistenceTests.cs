using ChatClient.Api.Services;
using ChatClient.Api.Services.Scheduling;
using ChatClient.Domain.Models;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;

namespace ChatClient.Tests;

public class ScheduledTaskPersistenceTests
{
    private static ScheduledTaskRepository Repository(string file) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ScheduledTasks:FilePath"] = file
        }).Build(), NullLogger<ScheduledTaskRepository>.Instance);

    [Fact]
    public async Task ParallelUpdates_AcrossRepositoryInstances_DoNotLoseRecords()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ollamachat-timers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "timers.json");
            var a = Repository(file);
            var b = Repository(file);
            await Task.WhenAll(Enumerable.Range(0, 24).Select(index =>
                (index % 2 == 0 ? a : b).UpdateAsync(items =>
                {
                    items.Add(new ScheduledTaskDefinition { Id = Guid.NewGuid(), Name = "Timer " + index });
                    return true;
                })));
            Assert.Equal(24, (await a.GetAllAsync()).Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task InvalidDocument_BlocksMutation_AndPreservesFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ollamachat-timers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "timers.json");
            await File.WriteAllTextAsync(file, "{ \"unexpected\": true }");
            var repo = Repository(file);
            await Assert.ThrowsAnyAsync<Exception>(async () => await repo.GetAllAsync());
            await Assert.ThrowsAnyAsync<Exception>(async () => await repo.UpdateAsync(items =>
            {
                items.Clear();
                return true;
            }));
            Assert.Equal("{ \"unexpected\": true }", await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task FailedMutation_LeavesPreviousJsonIntact()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ollamachat-timers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "timers.json");
            var repo = Repository(file);
            await repo.UpdateAsync(items => { items.Add(new ScheduledTaskDefinition { Id = Guid.NewGuid(), Name = "Kept" }); return true; });
            var before = await File.ReadAllTextAsync(file);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await repo.UpdateAsync<int>(_ => throw new InvalidOperationException("Fail before commit")));
            Assert.Equal(before, await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Service_SavesAndRegisters_WithoutResettingIntervalOnMetadataEdit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ollamachat-timers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var schedulerFactory = QuartzSchedulerBuilder.Create(q => q.UseInMemoryStore()).Build();
            var repository = Repository(Path.Combine(dir, "timers.json"));
            var service = new ScheduledTaskService(repository, schedulerFactory,
                new ScheduledTaskTriggerFactory(), new ScheduledTaskRuntimeState(),
                TimeProvider.System, NullLogger<ScheduledTaskService>.Instance);
            await service.InitializeAsync();
            var timer = await service.CreateAsync(new ScheduledTaskDefinition
            {
                Id = Guid.NewGuid(), Name = "First", Enabled = true,
                TimeZoneId = "Europe/Madrid",
                Schedule = new ScheduleDefinition { Type = ScheduleType.Interval, IntervalValue = 60 }
            });
            var scheduler = await schedulerFactory.GetScheduler();
            var key = ScheduledTaskTriggerFactory.TriggerKey(timer.Id);
            var before = (await scheduler.GetTrigger(key))?.NextFireTimeUtc;
            Assert.NotNull(before);
            timer.Name = "Renamed";
            timer = await service.UpdateAsync(timer);
            var after = (await scheduler.GetTrigger(key))?.NextFireTimeUtc;
            Assert.Equal(before, after);
            await service.SetEnabledAsync(timer.Id, timer.Revision, false);
            Assert.Null(await scheduler.GetTrigger(key));
            var disabled = Assert.Single(await service.GetAllAsync());
            Assert.Equal(ChatClient.Application.Services.ScheduledTaskStatus.Disabled, disabled.Status);
            await service.DeleteAsync(disabled.Definition.Id, disabled.Definition.Revision);
            Assert.Empty(await repository.GetAllAsync());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }


    [Fact]
    public async Task SavedWorkspaceBinding_PersistsAndDoesNotResetTimer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ollamachat-saved-workspace-timer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var schedulerFactory = QuartzSchedulerBuilder.Create(q => q.UseInMemoryStore()).Build();
            var repository = Repository(Path.Combine(dir, "timers.json"));
            var service = new ScheduledTaskService(repository, schedulerFactory,
                new ScheduledTaskTriggerFactory(), new ScheduledTaskRuntimeState(),
                TimeProvider.System, NullLogger<ScheduledTaskService>.Instance);
            await service.InitializeAsync();
            var agentId = Guid.NewGuid();
            var timer = await service.CreateAsync(new ScheduledTaskDefinition
            {
                Id = Guid.NewGuid(), Name = "Workspace task", Enabled = true,
                TimeZoneId = "Europe/Madrid",
                Schedule = new ScheduleDefinition { Type = ScheduleType.Interval, IntervalValue = 60 },
                AgentExecution = new ScheduledAgentConfiguration { AgentId = agentId, Prompt = "Read README" }
            });
            var scheduler = await schedulerFactory.GetScheduler();
            var key = ScheduledTaskTriggerFactory.TriggerKey(timer.Id);
            var before = (await scheduler.GetTrigger(key))?.NextFireTimeUtc;
            Assert.NotNull(before);

            var workspaceId = Guid.NewGuid();
            timer.AgentExecution!.WorkspaceId = workspaceId;
            timer = await service.UpdateAsync(timer);
            Assert.Equal(before, (await scheduler.GetTrigger(key))?.NextFireTimeUtc);

            var restored = Assert.Single(await Repository(Path.Combine(dir, "timers.json")).GetAllAsync());
            Assert.Equal(workspaceId, restored.AgentExecution!.WorkspaceId);
            Assert.Equal(agentId, restored.AgentExecution.AgentId);
            Assert.Equal("Read README", restored.AgentExecution.Prompt);
            var legacy = System.Text.Json.JsonSerializer.Deserialize<ScheduledAgentConfiguration>(
                "{\\"AgentId\\":\\"" + agentId + "\\",\\"Prompt\\":\\"Legacy\\"}")!;
            Assert.Null(legacy.WorkspaceId);
            Assert.Throws<ArgumentException>(() => ScheduledAgentConfigurationValidation.Validate(
                new ScheduledAgentConfiguration { AgentId = agentId, Prompt = "Test", WorkspaceId = Guid.Empty }));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task InvalidAgentBinding_DoesNotBlockEnablingQuartzSchedule()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ollamachat-timers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var schedulerFactory = QuartzSchedulerBuilder.Create(q => q.UseInMemoryStore()).Build();
            var repository = Repository(Path.Combine(dir, "timers.json"));
            var id = Guid.NewGuid();
            await repository.UpdateAsync(items =>
            {
                items.Add(new ScheduledTaskDefinition
                {
                    Id = id, Name = "Repairable", Enabled = false, Revision = 1,
                    TimeZoneId = "Europe/Madrid",
                    Schedule = new ScheduleDefinition { Type = ScheduleType.Interval, IntervalValue = 15 },
                    AgentExecution = new ScheduledAgentConfiguration { AgentId = Guid.Empty, Prompt = "" }
                });
                return true;
            });
            var service = new ScheduledTaskService(repository, schedulerFactory,
                new ScheduledTaskTriggerFactory(), new ScheduledTaskRuntimeState(),
                TimeProvider.System, NullLogger<ScheduledTaskService>.Instance);
            await service.InitializeAsync();
            var enabled = await service.SetEnabledAsync(id, expectedRevision: 1, enabled: true);
            Assert.True(enabled.Enabled);
            Assert.Equal(ScheduledAgentConfigurationValidation.MaximumPromptLength, 32 * 1024);
            var view = Assert.Single(await service.GetAllAsync());
            Assert.Equal(ChatClient.Application.Services.ScheduledTaskStatus.Active, view.Status);
            Assert.Equal(Guid.Empty, view.Definition.AgentExecution!.AgentId);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Service_RejectsStaleRevision()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ollamachat-timers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var factory = QuartzSchedulerBuilder.Create(q => q.UseInMemoryStore()).Build();
            var service = new ScheduledTaskService(Repository(Path.Combine(dir, "timers.json")),
                factory, new ScheduledTaskTriggerFactory(), new ScheduledTaskRuntimeState(),
                TimeProvider.System, NullLogger<ScheduledTaskService>.Instance);
            await service.InitializeAsync();
            var saved = await service.CreateAsync(new ScheduledTaskDefinition
            {
                Id = Guid.NewGuid(), Name = "Test", Enabled = false,
                Schedule = new ScheduleDefinition { Type = ScheduleType.Daily, Hour = 14 },
                TimeZoneId = "Europe/Madrid"
            });
            var stale = System.Text.Json.JsonSerializer.Deserialize<ScheduledTaskDefinition>(
                System.Text.Json.JsonSerializer.Serialize(saved))!;
            saved.Name = "Updated";
            await service.UpdateAsync(saved);
            stale.Name = "Stale change";
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.UpdateAsync(stale));
            Assert.Equal("Updated", Assert.Single(await service.GetAllAsync()).Definition.Name);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
