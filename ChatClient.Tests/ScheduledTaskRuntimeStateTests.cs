using ChatClient.Api.Services.Scheduling;

namespace ChatClient.Tests;

public sealed class ScheduledTaskRuntimeStateTests
{
    [Fact]
    public async Task JobAcceptanceRejectsDisabledAndPreviousGeneration()
    {
        var runtime = new ScheduledTaskRuntimeState();
        var id = Guid.NewGuid();
        var first = DateTimeOffset.Parse("2027-01-01T00:00:00Z");
        runtime.Set(id, new ScheduledTaskRuntimeState.TimerState
        {
            Enabled = true, Generation = 1, Name = "First"
        });
        var accepted = await runtime.TryRecordAsync(id, 1, first, TestContext.Current.CancellationToken);
        Assert.True(accepted.Valid);
        Assert.Equal("First", accepted.Name);

        runtime.Set(id, new ScheduledTaskRuntimeState.TimerState
        {
            Enabled = true, Generation = 2, Name = "Rescheduled"
        });
        Assert.False((await runtime.TryRecordAsync(id, 1, first.AddMinutes(1),
            TestContext.Current.CancellationToken)).Valid);

        runtime.Set(id, new ScheduledTaskRuntimeState.TimerState
        {
            Enabled = false, Generation = 3, Name = "Disabled"
        });
        Assert.False((await runtime.TryRecordAsync(id, 2, first.AddMinutes(2),
            TestContext.Current.CancellationToken)).Valid);

        runtime.Remove(id);
        Assert.False((await runtime.TryRecordAsync(id, 3, first.AddMinutes(3),
            TestContext.Current.CancellationToken)).Valid);
    }

    [Fact]
    public void OrphanCleanupTrackingCanBeRetriedWithoutDuplicateIds()
    {
        var runtime = new ScheduledTaskRuntimeState();
        var id = Guid.NewGuid();
        runtime.MarkCleanup(id, "Quartz delete failed");
        runtime.MarkCleanup(id, "Quartz delete failed again");
        Assert.Equal(id, Assert.Single(runtime.PendingCleanup()));
        runtime.ClearCleanup(id);
        Assert.Empty(runtime.PendingCleanup());
    }
}
