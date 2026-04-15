using ChatClient.Tests.Experiments.ThreeLayerPlanning.Scenarios;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;
using ChatClient.Tests;
using Xunit.Abstractions;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Tests;

public sealed class ThreeLayerPlannerVacuumWorkflowTests(ITestOutputHelper output)
{
    [RealWebFact]
    [Trait("Category", "RealWebExploration")]
    public async Task VacuumScenario_WithLiveModel_ProducesExperimentRun()
    {
        var scenario = VacuumMopUnder600Scenario.Create();
        var experiment = new ThreeLayerPlanningExperiment(
            ThreeLayerTestRuntimeFactory.CreateLiveLlmClient(),
            ThreeLayerTestRuntimeFactory.CreateRealWebTools(new TestHttpClientFactory()));

        var run = await experiment.RunAsync(scenario, runIndex: 1);

        output.WriteLine($"status={run.Status}");
        output.WriteLine($"outlineShape={run.OutlineShape}");
        output.WriteLine($"lowLevelShape={run.LowLevelShape}");
        output.WriteLine($"runtimeShape={run.RuntimeShape}");
        output.WriteLine($"issues={string.Join(" | ", run.Issues)}");

        Assert.Equal(1, run.RunIndex);
        Assert.False(string.IsNullOrWhiteSpace(run.Status));
        Assert.False(string.IsNullOrWhiteSpace(run.OutlinePlanJson));
        Assert.False(string.IsNullOrWhiteSpace(run.LowLevelPlanJson));
    }
}
