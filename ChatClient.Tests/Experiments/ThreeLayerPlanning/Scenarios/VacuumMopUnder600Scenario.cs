namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Scenarios;

public sealed class ThreeLayerExperimentScenario
{
    public string ScenarioId { get; init; } = string.Empty;

    public string UserQuery { get; init; } = string.Empty;

    public string ResultExpectations { get; init; } = string.Empty;

    public List<string> RequiredDeliverables { get; init; } = [];

    public string ExpectedFinalArtifactType { get; init; } = "string";
}

public static class VacuumMopUnder600Scenario
{
    public static ThreeLayerExperimentScenario Create() =>
        new()
        {
            ScenarioId = "vacuum-mop-under-600",
            UserQuery = "Посоветуй хороший робот пылесос с мойкой до 600 EUR",
            ResultExpectations =
                "Build a user-visible answer. The result should be a short final recommendation based on collected evidence, " +
                "not only an intermediate shortlist. The workflow should naturally separate discovery, acquisition, " +
                "evidence extraction, filtering, and answer generation when those stages are needed.",
            RequiredDeliverables =
            [
                "candidate products",
                "price filtering under 600 EUR",
                "washing support verification",
                "final answer"
            ],
            ExpectedFinalArtifactType = "string"
        };
}
