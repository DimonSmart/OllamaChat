using ChatClient.Tests.Experiments.ThreeLayerPlanning.Scenarios;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;
using ChatClient.Tests;
using Xunit.Abstractions;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Tests;

public sealed class ThreeLayerPlannerRepeatabilityTests(ITestOutputHelper output)
{
    private const int DefaultRunCount = 3;
    private const string RunCountEnvironmentVariable = "CHATCLIENT_THREE_LAYER_REPEATABILITY_RUNS";

    [RealWebFact]
    [Trait("Category", "RealWebExploration")]
    public async Task VacuumScenario_WithLiveModel_CapturesRepeatabilityReport()
    {
        var scenario = VacuumMopUnder600Scenario.Create();
        var experiment = new ThreeLayerPlanningExperiment(
            ThreeLayerTestRuntimeFactory.CreateLiveLlmClient(),
            ThreeLayerTestRuntimeFactory.CreateRealWebTools(new TestHttpClientFactory()));

        var runCount = ResolveRunCount();
        List<ThreeLayerExperimentRun> runs = [];
        for (var index = 1; index <= runCount; index++)
        {
            var run = await experiment.RunAsync(scenario, index);
            runs.Add(run);
            output.WriteLine($"run={index} status={run.Status} outline={run.OutlineShape} low={run.LowLevelShape} runtime={run.RuntimeShape}");
        }

        var report = ExperimentReportBuilder.Build(scenario.ScenarioId, runs);
        var paths = await new ThreeLayerExperimentArtifactWriter().SaveAsync(report);

        output.WriteLine($"summary={paths.SummaryPath}");
        output.WriteLine($"log={paths.LogPath}");

        Assert.Equal(runCount, report.RunCount);
        Assert.Equal(runCount, report.Runs.Count);
    }

    private static int ResolveRunCount()
    {
        var value = Environment.GetEnvironmentVariable(RunCountEnvironmentVariable);
        return int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : DefaultRunCount;
    }
}
