using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Models;

public sealed class CompactionProfileEditorState
{
    private readonly HashSet<CompactionStage> separateSummarizerStages = [];

    public void Initialize(CompactionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        separateSummarizerStages.Clear();

        foreach (var stage in profile.Stages.Where(static stage =>
                     stage.Kind == CompactionStageKinds.Summarization &&
                     stage.SummarizerLlmId is Guid id && id != Guid.Empty &&
                     !string.IsNullOrWhiteSpace(stage.SummarizerModelName)))
        {
            separateSummarizerStages.Add(stage);
        }
    }

    public bool UsesSeparateSummarizer(CompactionStage stage) => separateSummarizerStages.Contains(stage);

    public bool HasValidSummarizerConfiguration(CompactionStage stage)
    {
        if (stage.Kind != CompactionStageKinds.Summarization)
            return true;

        if (!UsesSeparateSummarizer(stage))
            return stage.SummarizerLlmId is null && string.IsNullOrWhiteSpace(stage.SummarizerModelName);

        return stage.SummarizerLlmId is Guid serverId &&
               serverId != Guid.Empty &&
               !string.IsNullOrWhiteSpace(stage.SummarizerModelName);
    }

    public void ToggleSeparateSummarizer(CompactionStage stage, bool useSeparate)
    {
        if (useSeparate)
        {
            separateSummarizerStages.Add(stage);
            return;
        }

        separateSummarizerStages.Remove(stage);
        stage.SummarizerLlmId = null;
        stage.SummarizerModelName = null;
    }

    public void SetSummarizerSelection(CompactionStage stage, Guid? serverId, string? modelName)
    {
        stage.SummarizerLlmId = serverId;
        stage.SummarizerModelName = modelName;
    }

    public void RemoveStage(CompactionStage stage) => separateSummarizerStages.Remove(stage);

    public void ChangeStageKind(CompactionStage stage, string kind)
    {
        var defaults = CompactionStageDefaults.Create(kind);
        stage.Kind = defaults.Kind;
        stage.Trigger = defaults.Trigger;
        stage.Target = defaults.Target;
        stage.MinimumPreservedGroups = defaults.MinimumPreservedGroups;
        stage.MinimumPreservedTurns = defaults.MinimumPreservedTurns;

        if (kind == CompactionStageKinds.Summarization)
            return;

        separateSummarizerStages.Remove(stage);
        stage.SummaryInstructions = null;
        stage.SummarizerLlmId = null;
        stage.SummarizerModelName = null;
    }

    public void ChangeProfileKind(CompactionProfile profile, string newKind)
    {
        if (newKind == CompactionProfileKinds.ContextWindow)
        {
            profile.Kind = newKind;
            profile.Stages.Clear();
            if (!HasValidContextWindowThresholds(profile))
            {
                profile.ToolResultThreshold = .50;
                profile.TruncationThreshold = .80;
            }
            return;
        }

        if (newKind == CompactionProfileKinds.CustomPipeline)
        {
            profile.Kind = newKind;
            if (profile.Stages.Count == 0)
            {
                profile.Stages =
                [
                    CompactionStageDefaults.Create(CompactionStageKinds.ToolResult),
                    CompactionStageDefaults.Create(CompactionStageKinds.Truncation)
                ];
            }
        }
    }

    private static bool HasValidContextWindowThresholds(CompactionProfile profile) =>
        profile.ToolResultThreshold is > 0 and <= 1 &&
        profile.TruncationThreshold is > 0 and <= 1 &&
        profile.TruncationThreshold >= profile.ToolResultThreshold;
}
