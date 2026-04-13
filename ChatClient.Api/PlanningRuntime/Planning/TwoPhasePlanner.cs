using System.Text;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;

namespace ChatClient.Api.PlanningRuntime.Planning;

public sealed class TwoPhasePlanner(
    IPlanningRequestAnalyzer requestAnalyzer,
    IPlanningDraftPlanner draftPlanner,
    IExecutionLogger? executionLogger = null,
    IPlanRunObserver? planRunObserver = null) : IPlanner
{
    private readonly IExecutionLogger _log = executionLogger ?? NullExecutionLogger.Instance;
    private readonly IPlanRunObserver _observer = planRunObserver ?? NullPlanRunObserver.Instance;

    public async Task<PlanDefinition> CreatePlanAsync(
        string userQuery,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userQuery);

        var analysis = await requestAnalyzer.AnalyzeAsync(userQuery, cancellationToken);
        var plannerInput = BuildPlannerInput(userQuery, analysis);

        _log.Log(
            $"[plan] analyze:handoff rewritten={Shorten(analysis.RewrittenRequest, 200)} deliverables={analysis.Deliverables.Count} outlineSteps={analysis.SuggestedPlanOutline.Count}");
        _observer.OnEvent(new DiagnosticPlanRunEvent(
            "planner",
            $"Planning handoff: rewritten request ready, deliverables={analysis.Deliverables.Count}, outlineSteps={analysis.SuggestedPlanOutline.Count}."));

        return await draftPlanner.CreatePlanAsync(
            new PlanningDraftPlannerRequest
            {
                OriginalUserQuery = userQuery,
                PlannerInput = plannerInput
            },
            cancellationToken);
    }

    internal static string BuildPlannerInput(string userQuery, PlanningRequestAnalysis analysis)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Use the original user request plus the internal planning analysis below to build the executable JSON plan.");
        sb.AppendLine("The planning analysis is an internal aid. It may clarify structure, but it does not add new external facts or new user requirements.");
        sb.AppendLine("If the planning analysis conflicts with the original request, follow the original request.");
        sb.AppendLine();
        sb.AppendLine("Original user request:");
        sb.AppendLine(userQuery.Trim());
        sb.AppendLine();
        sb.AppendLine("Internal planning analysis:");
        sb.AppendLine(PlanningJson.SerializeIndented(analysis));
        return sb.ToString().Trim();
    }

    private static string Shorten(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "<empty>";

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maxLength
            ? normalized
            : $"{normalized[..maxLength]}...";
    }
}
