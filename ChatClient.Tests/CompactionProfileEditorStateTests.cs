using ChatClient.Api.Client.Models;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public sealed class CompactionProfileEditorStateTests
{
    [Fact]
    public void ConfirmingContextWindowSwitch_RemovesPipelineAndPreservesValidThresholds()
    {
        var profile = CreatePipelineProfile();
        profile.ToolResultThreshold = .55;
        profile.TruncationThreshold = .85;
        var state = new CompactionProfileEditorState();

        state.ChangeProfileKind(profile, CompactionProfileKinds.ContextWindow);

        Assert.Equal(CompactionProfileKinds.ContextWindow, profile.Kind);
        Assert.Empty(profile.Stages);
        Assert.Equal(.55, profile.ToolResultThreshold);
        Assert.Equal(.85, profile.TruncationThreshold);
    }

    [Fact]
    public void CancelingContextWindowSwitch_LeavesPipelineUnchanged()
    {
        var profile = CreatePipelineProfile();
        var stages = profile.Stages.Select(CloneStage).ToList();

        Assert.Equal(CompactionProfileKinds.CustomPipeline, profile.Kind);
        Assert.Equal(stages, profile.Stages, StageComparer.Instance);
    }

    [Fact]
    public void ChangingToCustomPipeline_CreatesRecommendedStagesWithoutDuplicates()
    {
        var profile = new CompactionProfile { Kind = CompactionProfileKinds.ContextWindow };
        var state = new CompactionProfileEditorState();

        state.ChangeProfileKind(profile, CompactionProfileKinds.CustomPipeline);
        state.ChangeProfileKind(profile, CompactionProfileKinds.CustomPipeline);

        Assert.Equal(CompactionProfileKinds.CustomPipeline, profile.Kind);
        Assert.Collection(profile.Stages,
            stage => AssertStageEquals(CompactionStageDefaults.Create(CompactionStageKinds.ToolResult), stage),
            stage => AssertStageEquals(CompactionStageDefaults.Create(CompactionStageKinds.Truncation), stage));
    }

    [Theory]
    [InlineData(false, null, null, true)]
    [InlineData(true, null, null, false)]
    [InlineData(true, "server", null, false)]
    [InlineData(true, null, "model", false)]
    [InlineData(true, "server", "model", true)]
    public void SeparateSummarizer_RequiresACompleteSelection(bool useSeparate, string? server, string? model, bool expected)
    {
        var stage = CompactionStageDefaults.Create(CompactionStageKinds.Summarization);
        var state = new CompactionProfileEditorState();
        state.Initialize(new CompactionProfile { Stages = [stage] });
        state.ToggleSeparateSummarizer(stage, useSeparate);
        state.SetSummarizerSelection(stage, server is null ? null : Guid.NewGuid(), model);

        Assert.Equal(expected, state.HasValidSummarizerConfiguration(stage));
    }

    [Fact]
    public void DisablingSeparateSummarizer_ClearsSelection()
    {
        var stage = CompactionStageDefaults.Create(CompactionStageKinds.Summarization);
        var state = new CompactionProfileEditorState();
        state.Initialize(new CompactionProfile { Stages = [stage] });
        state.ToggleSeparateSummarizer(stage, true);
        state.SetSummarizerSelection(stage, Guid.NewGuid(), "model");

        state.ToggleSeparateSummarizer(stage, false);

        Assert.Null(stage.SummarizerLlmId);
        Assert.Null(stage.SummarizerModelName);
        Assert.True(state.HasValidSummarizerConfiguration(stage));
    }

    [Fact]
    public void ChangingToSummarization_UsesPrimaryModelByDefault()
    {
        var stage = CompactionStageDefaults.Create(CompactionStageKinds.ToolResult);
        stage.SummaryInstructions = "stale instructions";
        stage.SummarizerLlmId = Guid.NewGuid();
        stage.SummarizerModelName = "stale model";
        var state = new CompactionProfileEditorState();

        state.ChangeStageKind(stage, CompactionStageKinds.Summarization);

        Assert.Equal(CompactionStageKinds.Summarization, stage.Kind);
        Assert.False(state.UsesSeparateSummarizer(stage));
        Assert.Null(stage.SummaryInstructions);
        Assert.Null(stage.SummarizerLlmId);
        Assert.Null(stage.SummarizerModelName);
        Assert.True(state.HasValidSummarizerConfiguration(stage));
    }

    [Fact]
    public void InitializingAnotherProfile_DoesNotRetainSeparateSummarizerState()
    {
        var separateStage = CompactionStageDefaults.Create(CompactionStageKinds.Summarization);
        separateStage.SummarizerLlmId = Guid.NewGuid();
        separateStage.SummarizerModelName = "separate";
        var primaryStage = CompactionStageDefaults.Create(CompactionStageKinds.Summarization);
        var state = new CompactionProfileEditorState();

        state.Initialize(new CompactionProfile { Stages = [separateStage] });
        state.Initialize(new CompactionProfile { Stages = [primaryStage] });

        Assert.False(state.UsesSeparateSummarizer(primaryStage));
    }

    private static CompactionProfile CreatePipelineProfile() => new()
    {
        Kind = CompactionProfileKinds.CustomPipeline,
        Stages =
        [
            CompactionStageDefaults.Create(CompactionStageKinds.ToolResult),
            CompactionStageDefaults.Create(CompactionStageKinds.Truncation)
        ]
    };

    private static CompactionStage CloneStage(CompactionStage stage) => new()
    {
        Id = stage.Id,
        Kind = stage.Kind,
        Trigger = new CompactionLimit { Kind = stage.Trigger.Kind, Value = stage.Trigger.Value },
        Target = new CompactionLimit { Kind = stage.Target.Kind, Value = stage.Target.Value },
        MinimumPreservedGroups = stage.MinimumPreservedGroups,
        MinimumPreservedTurns = stage.MinimumPreservedTurns
    };

    private static void AssertStageEquals(CompactionStage expected, CompactionStage actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Trigger.Kind, actual.Trigger.Kind);
        Assert.Equal(expected.Trigger.Value, actual.Trigger.Value);
        Assert.Equal(expected.Target.Kind, actual.Target.Kind);
        Assert.Equal(expected.Target.Value, actual.Target.Value);
        Assert.Equal(expected.MinimumPreservedGroups, actual.MinimumPreservedGroups);
        Assert.Equal(expected.MinimumPreservedTurns, actual.MinimumPreservedTurns);
    }

    private sealed class StageComparer : IEqualityComparer<CompactionStage>
    {
        public static StageComparer Instance { get; } = new();

        public bool Equals(CompactionStage? x, CompactionStage? y)
        {
            if (x is null || y is null)
                return x is null && y is null;

            return x.Id == y.Id && x.Kind == y.Kind &&
                   x.Trigger.Kind == y.Trigger.Kind && x.Trigger.Value == y.Trigger.Value &&
                   x.Target.Kind == y.Target.Kind && x.Target.Value == y.Target.Value &&
                   x.MinimumPreservedGroups == y.MinimumPreservedGroups &&
                   x.MinimumPreservedTurns == y.MinimumPreservedTurns;
        }

        public int GetHashCode(CompactionStage obj) => obj.Id.GetHashCode();
    }
}
