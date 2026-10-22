using ChatClient.Api.Services.AgentRuntime;
using ChatClient.Api.Services.Scheduling;
using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ChatClient.Tests;

public sealed class ScheduledTaskManualRunTests
{
    [Fact]
    public void BindingValidation_RejectsEmptyAndOversize_AndAcceptsBoundary()
    {
        Assert.Throws<ArgumentException>(() => ScheduledAgentConfigurationValidation.Validate(null));
        Assert.Throws<ArgumentException>(() => ScheduledAgentConfigurationValidation.Validate(new()
        {
            AgentId = Guid.Empty, Prompt = "valid"
        }));
        Assert.Throws<ArgumentException>(() => ScheduledAgentConfigurationValidation.Validate(new()
        {
            AgentId = Guid.NewGuid(), Prompt = "   "
        }));
        var binding = new ScheduledAgentConfiguration
        {
            AgentId = Guid.NewGuid(),
            Prompt = new string('a', ScheduledAgentConfigurationValidation.MaximumPromptLength)
        };
        ScheduledAgentConfigurationValidation.Validate(binding);
        binding.Prompt += "!";
        Assert.Throws<ArgumentException>(() => ScheduledAgentConfigurationValidation.Validate(binding));
    }

    [Fact]
    public void Binding_SerializesInExistingTimer_AndLegacyJsonIsSupported()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<ScheduledTaskDefinition>(
            "{\"Name\":\"Legacy\",\"Enabled\":false}")!;
        Assert.Null(legacy.AgentExecution);
        var agentId = Guid.NewGuid();
        legacy.AgentExecution = new ScheduledAgentConfiguration { AgentId = agentId, Prompt = "go" };
        var restored = System.Text.Json.JsonSerializer.Deserialize<ScheduledTaskDefinition>(
            System.Text.Json.JsonSerializer.Serialize(legacy))!;
        Assert.Equal(agentId, restored.AgentExecution!.AgentId);
        Assert.Equal("go", restored.AgentExecution.Prompt);
        restored.AgentExecution = null;
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<ScheduledTaskDefinition>(
            System.Text.Json.JsonSerializer.Serialize(restored))!.AgentExecution);
    }

    [Fact]
    public void BindingEdit_DoesNotChangeQuartzTrigger()
    {
        var before = new ScheduledTaskDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Agent Timer",
            Schedule = new ScheduleDefinition { Type = ScheduleType.Interval, IntervalValue = 45 }
        };
        var after = System.Text.Json.JsonSerializer.Deserialize<ScheduledTaskDefinition>(
            System.Text.Json.JsonSerializer.Serialize(before))!;
        after.AgentExecution = new ScheduledAgentConfiguration { AgentId = Guid.NewGuid(), Prompt = "hi" };
        Assert.True(ScheduledTaskTriggerFactory.SameTrigger(before, after));
        after.AgentExecution.Prompt = "changed";
        Assert.True(ScheduledTaskTriggerFactory.SameTrigger(before, after));
    }

    [Fact]
    public async Task ManualRun_IsSingleFlight_AndCancelIsScopedToRunId()
    {
        var fake = new BlockingExecutor();
        using var services = new ServiceCollection()
            .AddSingleton<IHeadlessAgentExecutor>(fake)
            .BuildServiceProvider();
        var id = Guid.NewGuid();
        var repo = new Mock<IScheduledTaskRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScheduledTaskDefinition>
            {
                new()
                {
                    Id = id, Name = "Timer", Enabled = false,
                    AgentExecution = new ScheduledAgentConfiguration { AgentId = Guid.NewGuid(), Prompt = "hello" }
                }
            });
        var coordinator = new ScheduledTaskManualRunService(
            services.GetRequiredService<IServiceScopeFactory>(), repo.Object, new ScheduledTaskRuntimeState(),
            Options.Create(new ScheduledTaskManualRunOptions()), TimeProvider.System,
            NullLogger<ScheduledTaskManualRunService>.Instance);
        var started = await coordinator.RunNowAsync(id);
        Assert.True(started.Accepted);
        Assert.False((await coordinator.RunNowAsync(Guid.NewGuid())).Accepted);
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(coordinator.Cancel(Guid.NewGuid()));
        Assert.True(coordinator.Cancel(started.Run!.ManualRunId));
        Assert.False(coordinator.Cancel(started.Run.ManualRunId));
        await WaitForTerminal(coordinator, started.Run.ManualRunId);
        Assert.Equal(ManualRunStatus.Canceled, coordinator.Find(started.Run.ManualRunId)!.Status);
        await coordinator.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeletedTimer_CancelsRunAndErasesLastResult()
    {
        var fake = new BlockingExecutor();
        using var services = new ServiceCollection()
            .AddSingleton<IHeadlessAgentExecutor>(fake).BuildServiceProvider();
        var id = Guid.NewGuid();
        var repo = new Mock<IScheduledTaskRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScheduledTaskDefinition>
            {
                new() { Id = id, Name = "Timer", AgentExecution = new()
                    { AgentId = Guid.NewGuid(), Prompt = "hi" } }
            });
        var coordinator = new ScheduledTaskManualRunService(
            services.GetRequiredService<IServiceScopeFactory>(), repo.Object, new ScheduledTaskRuntimeState(),
            Options.Create(new ScheduledTaskManualRunOptions()), TimeProvider.System,
            NullLogger<ScheduledTaskManualRunService>.Instance);
        var started = await coordinator.RunNowAsync(id);
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.TimerDeleted(id);
        await WaitUntil(() => coordinator.Current is null);
        Assert.Null(coordinator.ForTimer(id));
        Assert.Null(coordinator.Find(started.Run!.ManualRunId));
    }

    [Fact]
    public async Task HeadlessApproval_AlwaysDeniesAndRecordsRequest()
    {
        var approval = new HeadlessToolApprovalCoordinator();
        var request = new ChatClient.Application.Services.Agentic.SessionToolApprovalRequest(
            "approval", "dangerous", "agent", "secret", ChatClient.Application.Services.Agentic.ToolApprovalSessionScope.Tool, null);
        Assert.Equal(ChatClient.Application.Services.Agentic.ToolApprovalDecision.Deny,
            await approval.RequestApprovalAsync(request));
        Assert.Equal(1, approval.RequestCount);
        Assert.Null(approval.PendingRequest);
        Assert.False(approval.TryRespond("approval",
            ChatClient.Application.Services.Agentic.ToolApprovalDecision.ApproveOnce, _ => { }));
    }

    private static async Task WaitForTerminal(ScheduledTaskManualRunService coordinator, Guid id)
        => await WaitUntil(() => coordinator.Find(id) is { Status: not ManualRunStatus.Running });

    private static async Task WaitUntil(Func<bool> complete)
    {
        for (var i = 0; i < 300 && !complete(); i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(complete(), "Manual run did not finish.");
    }

    private sealed class BlockingExecutor : IHeadlessAgentExecutor
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HeadlessAgentRunResult> ExecuteAsync(
            HeadlessAgentRunRequest request, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            return new HeadlessAgentRunResult("agent", null, "done", null, null);
        }
    }
}
