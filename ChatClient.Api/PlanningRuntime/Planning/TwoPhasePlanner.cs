using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using System.Text;

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

        var brief = await requestAnalyzer.AnalyzeAsync(userQuery, cancellationToken);
        var plannerInput = BuildPlannerInput(userQuery, brief);

        _log.Log(
            $"[plan] analyze:handoff rewritten={Shorten(brief.RewrittenRequest, 200)} deliverables={brief.Deliverables.Count} outlineSteps={brief.SuggestedPlanOutline.Count}");
        _observer.OnEvent(new DiagnosticPlanRunEvent(
            "planner",
            $"Planning handoff: rewritten request ready, deliverables={brief.Deliverables.Count}, outlineSteps={brief.SuggestedPlanOutline.Count}."));

        var draft = await draftPlanner.CreatePlanAsync(
            new PlanningDraftPlannerRequest
            {
                OriginalUserQuery = userQuery,
                PlannerInput = plannerInput,
                RequestBrief = brief
            },
            cancellationToken);

        return AttachResultContract(draft, brief);
    }

    /// <summary>
    /// Derives a <see cref="ResultContract"/> from the request brief and attaches it
    /// to the generated plan draft. The contract is not part of the LLM-generated JSON;
    /// it is injected by the framework after the draft is produced.
    /// </summary>
    private static PlanDefinition AttachResultContract(PlanDefinition draft, RequestBrief brief)
    {
        var contract = ResultContractDeriver.DeriveFrom(brief);
        return new PlanDefinition
        {
            Goal = draft.Goal,
            Steps = draft.Steps,
            ResultContract = contract,
            BlockedReason = draft.BlockedReason
        };
    }

    internal static string BuildPlannerInput(string userQuery, RequestBrief brief)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Use the original user request plus the structured planning brief below to build the executable JSON plan.");
        sb.AppendLine("The planning brief is an internal aid. It may clarify structure, but it does not add new external facts or new user requirements.");
        sb.AppendLine("If the planning brief conflicts with the original request, follow the original request.");
        sb.AppendLine();
        sb.AppendLine("Original user request:");
        sb.AppendLine(userQuery.Trim());
        sb.AppendLine();
        sb.AppendLine("Structured planning brief:");
        sb.AppendLine(PlanningPromptCompiler.Compile(brief));
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
