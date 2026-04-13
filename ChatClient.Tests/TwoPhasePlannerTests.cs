using ChatClient.Api.PlanningRuntime.Planning;

namespace ChatClient.Tests;

public sealed class TwoPhasePlannerTests
{
    [Fact]
    public async Task CreatePlanAsync_AnalyzesRequest_AndPassesPreparedContextToDraftPlanner()
    {
        const string userQuery = "Find three robot kits that can be programmed with Python and compare them.";
        var brief = new RequestBrief
        {
            RewrittenRequest = "Find at least three Python-programmable robot kits, compare them, and recommend one.",
            Goal = "Produce a comparison-based recommendation.",
            Deliverables = ["three robot kits", "comparison", "recommendation"],
            Constraints = ["at least three items"],
            AcquisitionNeeds = ["official product information for each kit"],
            ReasoningNeeds = ["compare key trade-offs", "pick a recommendation"],
            SuggestedPlanOutline = ["discover candidate kits", "gather source material", "compare and recommend"]
        };
        var draftPlan = new PlanDefinition
        {
            Goal = "compare robot kits",
            Steps =
            [
                new PlanStep
                {
                    Id = "final",
                    Kind = PlanStepKinds.Llm,
                    In = []
                }
            ]
        };
        var analyzer = new RecordingPlanningRequestAnalyzer(brief);
        var draftPlanner = new RecordingDraftPlanner(draftPlan);
        var planner = new TwoPhasePlanner(analyzer, draftPlanner);

        var result = await planner.CreatePlanAsync(userQuery);

        // TwoPhasePlanner attaches a ResultContract so the returned instance is not the same reference,
        // but the goal and steps must be preserved.
        Assert.Equal(draftPlan.Goal, result.Goal);
        Assert.Equal(draftPlan.Steps.Count, result.Steps.Count);
        Assert.NotNull(result.ResultContract);

        Assert.Equal(1, analyzer.CallCount);
        Assert.Equal(userQuery, analyzer.LastUserQuery);
        Assert.NotNull(draftPlanner.LastRequest);
        Assert.Equal(userQuery, draftPlanner.LastRequest!.OriginalUserQuery);
        Assert.Contains("Original user request:", draftPlanner.LastRequest.PlannerInput, StringComparison.Ordinal);
        Assert.Contains(userQuery, draftPlanner.LastRequest.PlannerInput, StringComparison.Ordinal);
        Assert.Contains(brief.RewrittenRequest, draftPlanner.LastRequest.PlannerInput, StringComparison.Ordinal);
        Assert.Contains("Suggested plan outline", draftPlanner.LastRequest.PlannerInput, StringComparison.Ordinal);
        Assert.Contains("discover candidate kits", draftPlanner.LastRequest.PlannerInput, StringComparison.Ordinal);
        Assert.Same(brief, draftPlanner.LastRequest.RequestBrief);
    }

    [Fact]
    public void BuildPlannerInput_UsesBriefAsPlanningAid_NotAsReplacementForOriginalRequest()
    {
        const string userQuery = "Download two files and summarize them.";
        var brief = new RequestBrief
        {
            RewrittenRequest = "Obtain two files, read them, and produce a summary.",
            Goal = "Return a summary of both files.",
            Deliverables = ["summary of two files"],
            Constraints = ["exactly two files"],
            AcquisitionNeeds = ["download both files"],
            ReasoningNeeds = ["summarize combined content"],
            SuggestedPlanOutline = ["download files", "summarize content"]
        };

        var plannerInput = TwoPhasePlanner.BuildPlannerInput(userQuery, brief);

        Assert.Contains("Original user request:", plannerInput, StringComparison.Ordinal);
        Assert.Contains(userQuery, plannerInput, StringComparison.Ordinal);
        Assert.Contains("planning brief", plannerInput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(brief.RewrittenRequest, plannerInput, StringComparison.Ordinal);
        Assert.Contains("does not add new external facts or new user requirements", plannerInput, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingPlanningRequestAnalyzer(RequestBrief brief) : IPlanningRequestAnalyzer
    {
        public int CallCount { get; private set; }

        public string? LastUserQuery { get; private set; }

        public Task<RequestBrief> AnalyzeAsync(
            string userQuery,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastUserQuery = userQuery;
            return Task.FromResult(brief);
        }
    }

    private sealed class RecordingDraftPlanner(PlanDefinition resultPlan) : IPlanningDraftPlanner
    {
        public PlanningDraftPlannerRequest? LastRequest { get; private set; }

        public Task<PlanDefinition> CreatePlanAsync(
            PlanningDraftPlannerRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(resultPlan);
        }
    }
}
