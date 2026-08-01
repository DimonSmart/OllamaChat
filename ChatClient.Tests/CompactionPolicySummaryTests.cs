using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class CompactionPolicySummaryTests
{
    [Fact]
    public void FormatAbsoluteThresholds_ResolvesPercentagesAgainstInputBudget()
    {
        var profile = new CompactionProfile
        {
            Kind = CompactionProfileKinds.ContextWindow,
            ToolResultThreshold = .50,
            TruncationThreshold = .80
        };

        var thresholds = CompactionPolicySummary.FormatAbsoluteThresholds(
            profile,
            new CompactionBudget(128_000, 8_000, 120_000));

        Assert.Equal("tool results=60000, history=96000", thresholds);
    }

    [Fact]
    public void FormatPolicy_UsesHumanReadableStageNames()
    {
        var profile = new CompactionProfile
        {
            Kind = CompactionProfileKinds.CustomPipeline,
            Stages = [
                CompactionStageDefaults.Create(CompactionStageKinds.ToolResult),
                CompactionStageDefaults.Create(CompactionStageKinds.Truncation)
            ]
        };

        Assert.Equal("tool results → history", CompactionPolicySummary.FormatPolicy(profile));
    }

    [Fact]
    public void FormatAbsoluteThresholds_FormatsInputBudgetFractionsAsPercentages()
    {
        var profile = new CompactionProfile
        {
            Kind = CompactionProfileKinds.CustomPipeline,
            Stages = [CompactionStageDefaults.Create(CompactionStageKinds.ToolResult)]
        };

        var thresholds = CompactionPolicySummary.FormatAbsoluteThresholds(
            profile,
            new CompactionBudget(128_000, 8_000, 120_001));

        Assert.Equal("tool results=54000 (45%)→42000 (35%)", thresholds);
    }
}
