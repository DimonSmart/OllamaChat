using System.Text.Json;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;

public static class ExperimentReportBuilder
{
    public static ThreeLayerExperimentReport Build(
        string scenarioId,
        IReadOnlyList<ThreeLayerExperimentRun> runs)
    {
        var distinctOutlineShapes = CountDistinct(runs.Select(static run => run.OutlineShape));
        var distinctLowLevelShapes = CountDistinct(runs.Select(static run => run.LowLevelShape));
        var distinctRuntimeShapes = CountDistinct(runs.Select(static run => run.RuntimeShape));

        return new ThreeLayerExperimentReport
        {
            ScenarioId = scenarioId,
            RunCount = runs.Count,
            OutlineValidCount = runs.Count(static run => run.OutlineValid),
            LowLevelValidCount = runs.Count(static run => run.LowLevelValid),
            RuntimeCompiledCount = runs.Count(static run => run.RuntimeCompiled),
            RuntimeExecutedCount = runs.Count(static run => run.RuntimeExecuted),
            DistinctOutlineShapes = distinctOutlineShapes,
            DistinctLowLevelShapes = distinctLowLevelShapes,
            DistinctRuntimeShapes = distinctRuntimeShapes,
            Runs = runs.ToList(),
            Conclusion = BuildConclusion(runs, distinctOutlineShapes, distinctLowLevelShapes, distinctRuntimeShapes)
        };
    }

    public static string BuildLog(ThreeLayerExperimentReport report)
    {
        var lines = new List<string>
        {
            $"scenarioId: {report.ScenarioId}",
            $"runCount: {report.RunCount}",
            $"outlineValidCount: {report.OutlineValidCount}",
            $"lowLevelValidCount: {report.LowLevelValidCount}",
            $"runtimeCompiledCount: {report.RuntimeCompiledCount}",
            $"runtimeExecutedCount: {report.RuntimeExecutedCount}",
            $"distinctOutlineShapes: {report.DistinctOutlineShapes}",
            $"distinctLowLevelShapes: {report.DistinctLowLevelShapes}",
            $"distinctRuntimeShapes: {report.DistinctRuntimeShapes}",
            string.Empty
        };

        foreach (var run in report.Runs.OrderBy(static run => run.RunIndex))
        {
            lines.Add($"=== Run {run.RunIndex} ===");
            lines.Add($"status: {run.Status}");
            lines.Add($"outlineValid: {run.OutlineValid}");
            lines.Add($"lowLevelValid: {run.LowLevelValid}");
            lines.Add($"runtimeCompiled: {run.RuntimeCompiled}");
            lines.Add($"runtimeExecuted: {run.RuntimeExecuted}");
            lines.Add($"outlineShape: {run.OutlineShape}");
            lines.Add($"lowLevelShape: {run.LowLevelShape}");
            lines.Add($"runtimeShape: {run.RuntimeShape}");
            lines.Add($"finalArtifactType: {run.FinalArtifactType}");
            if (!string.IsNullOrWhiteSpace(run.ResultStepId))
                lines.Add($"resultStepId: {run.ResultStepId}");
            if (run.Issues.Count > 0)
                lines.Add($"issues: {string.Join(" | ", run.Issues)}");
            if (!string.IsNullOrWhiteSpace(run.OutlineRawResponse))
            {
                lines.Add("outlineRawResponse:");
                lines.Add(run.OutlineRawResponse);
            }
            if (!string.IsNullOrWhiteSpace(run.LowLevelRawResponse))
            {
                lines.Add("lowLevelRawResponse:");
                lines.Add(run.LowLevelRawResponse);
            }
            if (!string.IsNullOrWhiteSpace(run.FinalOutputJson))
            {
                lines.Add("finalOutput:");
                lines.Add(run.FinalOutputJson);
            }

            lines.Add(string.Empty);
        }

        lines.Add("conclusion:");
        lines.Add(report.Conclusion);
        return string.Join(Environment.NewLine, lines);
    }

    private static int CountDistinct(IEnumerable<string> values) =>
        values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Count();

    private static string BuildConclusion(
        IReadOnlyList<ThreeLayerExperimentRun> runs,
        int distinctOutlineShapes,
        int distinctLowLevelShapes,
        int distinctRuntimeShapes)
    {
        var issueGroups = runs
            .SelectMany(static run => run.Issues)
            .GroupBy(static issue => issue, StringComparer.Ordinal)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.Ordinal)
            .ToList();

        var dominantIssue = issueGroups.FirstOrDefault()?.Key ?? "no dominant issue";
        return JsonSerializer.Serialize(new
        {
            summary = $"outlineShapes={distinctOutlineShapes}, lowLevelShapes={distinctLowLevelShapes}, runtimeShapes={distinctRuntimeShapes}",
            dominantIssue,
            executedRuns = runs.Count(static run => run.RuntimeExecuted),
            compiledRuns = runs.Count(static run => run.RuntimeCompiled)
        }, ExperimentJson.SerializerOptions);
    }
}
