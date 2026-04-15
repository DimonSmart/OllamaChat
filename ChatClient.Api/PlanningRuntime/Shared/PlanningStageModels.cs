using System.Text.Json.Nodes;

namespace ChatClient.Api.PlanningRuntime.Shared;

public sealed class PlanningJsonGenerationResult<T>
{
    public required T Result { get; init; }

    public string RawResponse { get; init; } = string.Empty;

    public JsonNode? RawJson { get; init; }
}

public sealed class PlanningStageResult<T>
{
    public required T Plan { get; init; }

    public string RawResponse { get; init; } = string.Empty;
}

public sealed class PlanningContractException : InvalidOperationException
{
    public PlanningContractException(
        string stage,
        IReadOnlyList<string> contractIssues,
        string rawResponse,
        string? materializedJson)
        : base($"{stage} contract validation failed: {string.Join("; ", contractIssues)}")
    {
        Stage = stage;
        ContractIssues = contractIssues;
        RawResponse = rawResponse;
        MaterializedJson = materializedJson;
    }

    public string Stage { get; }

    public IReadOnlyList<string> ContractIssues { get; }

    public string RawResponse { get; }

    public string? MaterializedJson { get; }
}
