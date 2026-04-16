using ChatClient.Api.Services;

namespace ChatClient.Api.PlanningRuntime.Outline;

internal sealed record OutlineNodeExecutionContract(
    string NodeKind,
    bool AllowsLlm,
    bool RequiresTerminalResult,
    AppToolPlannerRole? RequiredRole,
    AppToolProducesKind? RequiredProduces,
    string Description);

internal static class OutlineNodeExecutionContractResolver
{
    public static OutlineNodeExecutionContract Resolve(string? nodeKind)
    {
        var normalized = nodeKind?.Trim().ToLowerInvariant();
        return normalized switch
        {
            OutlineNodeKinds.Discover => new OutlineNodeExecutionContract(
                OutlineNodeKinds.Discover,
                AllowsLlm: false,
                RequiresTerminalResult: false,
                RequiredRole: AppToolPlannerRole.Discover,
                RequiredProduces: AppToolProducesKind.Reference,
                Description: "discover nodes must use a discover capability that produces references."),
            OutlineNodeKinds.Acquire => new OutlineNodeExecutionContract(
                OutlineNodeKinds.Acquire,
                AllowsLlm: false,
                RequiresTerminalResult: false,
                RequiredRole: AppToolPlannerRole.Acquire,
                RequiredProduces: AppToolProducesKind.Document,
                Description: "acquire nodes must use an acquire capability that turns references into documents or content."),
            OutlineNodeKinds.Extract => CreateTransformContract(OutlineNodeKinds.Extract),
            OutlineNodeKinds.Filter => CreateTransformContract(OutlineNodeKinds.Filter),
            OutlineNodeKinds.Rank => CreateTransformContract(OutlineNodeKinds.Rank),
            OutlineNodeKinds.Synthesize => CreateTransformContract(OutlineNodeKinds.Synthesize),
            OutlineNodeKinds.Act => new OutlineNodeExecutionContract(
                OutlineNodeKinds.Act,
                AllowsLlm: false,
                RequiresTerminalResult: false,
                RequiredRole: AppToolPlannerRole.Act,
                RequiredProduces: AppToolProducesKind.SideEffect,
                Description: "act nodes must use an action capability."),
            OutlineNodeKinds.Answer => new OutlineNodeExecutionContract(
                OutlineNodeKinds.Answer,
                AllowsLlm: true,
                RequiresTerminalResult: true,
                RequiredRole: null,
                RequiredProduces: null,
                Description: "answer nodes must end in a terminal llm step that produces the user-facing result."),
            _ => new OutlineNodeExecutionContract(
                normalized ?? string.Empty,
                AllowsLlm: false,
                RequiresTerminalResult: false,
                RequiredRole: null,
                RequiredProduces: null,
                Description: "unknown outline node kind")
        };
    }

    private static OutlineNodeExecutionContract CreateTransformContract(string nodeKind) =>
        new(
            nodeKind,
            AllowsLlm: true,
            RequiresTerminalResult: false,
            RequiredRole: AppToolPlannerRole.Transform,
            RequiredProduces: AppToolProducesKind.StructuredData,
            Description: $"{nodeKind} nodes must use llm or transform capabilities.");
}
