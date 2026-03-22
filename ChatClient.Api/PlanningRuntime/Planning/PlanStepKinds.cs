namespace ChatClient.Api.PlanningRuntime.Planning;

public static class PlanStepKinds
{
    public const string Tool = "tool";
    public const string Llm = "llm";
    public const string Agent = "agent";

    public static string GetKind(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (!string.IsNullOrWhiteSpace(step.Tool))
            return Tool;
        if (!string.IsNullOrWhiteSpace(step.Agent))
            return Agent;

        return Llm;
    }

    public static string GetName(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return step.Tool
            ?? step.Agent
            ?? step.Llm
            ?? string.Empty;
    }
}
