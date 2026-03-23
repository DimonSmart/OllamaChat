namespace ChatClient.Api.PlanningRuntime.Planning;

public static class PlanStepKinds
{
    public const string Tool = "tool";
    public const string Llm = "llm";
    public const string Agent = "agent";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case Tool:
                normalized = Tool;
                return true;
            case Llm:
                normalized = Llm;
                return true;
            case Agent:
                normalized = Agent;
                return true;
            default:
                return false;
        }
    }

    public static bool IsTool(PlanStep step) =>
        HasKind(step, Tool);

    public static bool IsLlm(PlanStep step) =>
        HasKind(step, Llm);

    public static bool IsAgent(PlanStep step) =>
        HasKind(step, Agent);

    public static string GetKind(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return TryNormalize(step.Kind, out var normalized)
            ? normalized
            : string.Empty;
    }

    public static string GetName(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return step.Name?.Trim() ?? string.Empty;
    }

    private static bool HasKind(PlanStep step, string expectedKind)
    {
        ArgumentNullException.ThrowIfNull(step);
        return TryNormalize(step.Kind, out var normalized)
            && string.Equals(normalized, expectedKind, StringComparison.Ordinal);
    }
}
