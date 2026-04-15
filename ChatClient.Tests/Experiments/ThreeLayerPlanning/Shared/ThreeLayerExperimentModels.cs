namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;

public sealed class ThreeLayerExperimentRun
{
    public int RunIndex { get; init; }

    public string OutlineShape { get; init; } = string.Empty;

    public string LowLevelShape { get; init; } = string.Empty;

    public string RuntimeShape { get; init; } = string.Empty;

    public bool OutlineValid { get; init; }

    public bool LowLevelValid { get; init; }

    public bool RuntimeCompiled { get; init; }

    public bool RuntimeExecuted { get; init; }

    public string Status { get; init; } = string.Empty;

    public string FinalArtifactType { get; init; } = "null";

    public string? ResultStepId { get; init; }

    public string OutlinePlanJson { get; init; } = string.Empty;

    public string? OutlineRawResponse { get; init; }

    public string LowLevelPlanJson { get; init; } = string.Empty;

    public string? LowLevelRawResponse { get; init; }

    public string? RuntimePlanJson { get; init; }

    public string? FinalOutputJson { get; init; }

    public List<string> Issues { get; init; } = [];
}

public sealed class ThreeLayerExperimentReport
{
    public string ScenarioId { get; init; } = string.Empty;

    public int RunCount { get; init; }

    public int OutlineValidCount { get; init; }

    public int LowLevelValidCount { get; init; }

    public int RuntimeCompiledCount { get; init; }

    public int RuntimeExecutedCount { get; init; }

    public int DistinctOutlineShapes { get; init; }

    public int DistinctLowLevelShapes { get; init; }

    public int DistinctRuntimeShapes { get; init; }

    public List<ThreeLayerExperimentRun> Runs { get; init; } = [];

    public string Conclusion { get; init; } = string.Empty;
}
