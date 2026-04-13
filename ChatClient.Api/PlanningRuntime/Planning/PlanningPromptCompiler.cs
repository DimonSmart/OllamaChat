using System.Text;

namespace ChatClient.Api.PlanningRuntime.Planning;

/// <summary>
/// Compiles a <see cref="RequestBrief"/> into a structured, human-readable planning
/// context block that the LLM draft planner uses as a contextual seed.
/// Replaces raw JSON serialization of the brief with a labelled section format
/// that is easier for the LLM to parse and reason about.
/// </summary>
internal static class PlanningPromptCompiler
{
    /// <summary>
    /// Formats the given brief as a structured planning context block.
    /// Sections with no entries are omitted to keep the prompt concise.
    /// </summary>
    public static string Compile(RequestBrief brief)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Goal: {brief.Goal.Trim()}");

        if (!string.IsNullOrWhiteSpace(brief.ExpectedResult))
            sb.AppendLine($"Expected result: {brief.ExpectedResult.Trim()}");

        sb.AppendLine($"Original request (rewritten): {brief.RewrittenRequest.Trim()}");

        AppendList(sb, "Deliverables", brief.Deliverables);
        AppendList(sb, "Constraints", brief.Constraints);
        AppendList(sb, "Acquisition needs", brief.AcquisitionNeeds);
        AppendList(sb, "Evidence requirements", brief.EvidenceRequirements);
        AppendList(sb, "Reasoning needs", brief.ReasoningNeeds);
        AppendList(sb, "Success criteria", brief.SuccessCriteria);

        if (!string.IsNullOrWhiteSpace(brief.OutputExpectations))
            sb.AppendLine($"Output expectations: {brief.OutputExpectations.Trim()}");

        AppendList(sb, "Ambiguity notes", brief.AmbiguityNotes);

        if (brief.SuggestedPlanOutline.Count > 0)
        {
            sb.AppendLine("Suggested plan outline (use as a skeleton — adapt to available tools):");
            for (var i = 0; i < brief.SuggestedPlanOutline.Count; i++)
                sb.AppendLine($"  {i + 1}. {brief.SuggestedPlanOutline[i].Trim()}");
        }

        return sb.ToString().Trim();
    }

    private static void AppendList(StringBuilder sb, string header, IReadOnlyCollection<string> items)
    {
        if (items.Count == 0)
            return;

        sb.AppendLine($"{header}:");
        foreach (var item in items)
            sb.AppendLine($"  - {item.Trim()}");
    }
}
