using ChatClient.Tests;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;

public sealed class ThreeLayerExperimentArtifactWriter
{
    private readonly string _artifactDirectory = Path.Combine(
        TestPathHelper.FindRepositoryRoot(),
        "artifacts",
        "three-layer-planning");

    public async Task<(string SummaryPath, string LogPath)> SaveAsync(
        ThreeLayerExperimentReport report,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_artifactDirectory);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var safeScenarioId = TestPathHelper.SanitizeFileName(report.ScenarioId);
        var prefix = Path.Combine(_artifactDirectory, $"{stamp}-{safeScenarioId}");
        var summaryPath = $"{prefix}.json";
        var logPath = $"{prefix}.log";

        await File.WriteAllTextAsync(summaryPath, ExperimentJson.SerializeIndented(report), cancellationToken);
        await File.WriteAllTextAsync(logPath, ExperimentReportBuilder.BuildLog(report), cancellationToken);
        return (summaryPath, logPath);
    }
}
