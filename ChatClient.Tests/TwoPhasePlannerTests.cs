using ChatClient.Api.PlanningRuntime.Planning;

namespace ChatClient.Tests;

public sealed class TwoPhasePlannerTests
{
    [Fact]
    public async Task CreatePlanAsync_AnalyzesRequest_AndPassesPreparedContextToDraftPlanner()
    {
        const string userQuery = "Find three robot kits that can be programmed with Python and compare them.";
        var analysis = new PlanningRequestAnalysis
        {
            RewrittenRequest = "Find at least three Python-programmable robot kits, compare them, and recommend one.",
            Goal = "Produce a comparison-based recommendation.",
            Deliverables = ["three robot kits", "comparison", "recommendation"],
            Constraints = ["at least three items"],
            AcquisitionNeeds = ["official product information for each kit"],
            ReasoningNeeds = ["compare key trade-offs", "pick a recommendation"],
            SuggestedPlanOutline = ["discover candidate kits", "gather source material", "compare and recommend"]
        };
        var expectedPlan = new PlanDefinition
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
        var analyzer = new RecordingPlanningRequestAnalyzer(analysis);
        var draftPlanner = new RecordingDraftPlanner(expectedPlan);
        var planner = new TwoPhasePlanner(analyzer, draftPlanner);

        var result = await planner.CreatePlanAsync(userQuery);

        Assert.Same(expectedPlan, result);
        Assert.Equal(1, analyzer.CallCount);
        Assert.Equal(userQuery, analyzer.LastUserQuery);
        Assert.NotNull(draftPlanner.LastRequest);
        Assert.Equal(userQuery, draftPlanner.LastRequest!.OriginalUserQuery);
        Assert.Contains("Original user request:", draftPlanner.LastRequest.PlannerInput, StringComparison.Ordinal);
        Assert.Contains(userQuery, draftPlanner.LastRequest.PlannerInput, StringComparison.Ordinal);
        Assert.Contains(analysis.RewrittenRequest, draftPlanner.LastRequest.PlannerInput, StringComparison.Ordinal);
        Assert.Contains("\"suggestedPlanOutline\"", draftPlanner.LastRequest.PlannerInput, StringComparison.Ordinal);
        Assert.Contains("discover candidate kits", draftPlanner.LastRequest.PlannerInput, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPlannerInput_UsesAnalysisAsPlanningAid_NotAsReplacementForOriginalRequest()
    {
        const string userQuery = "Download two files and summarize them.";
        var analysis = new PlanningRequestAnalysis
        {
            RewrittenRequest = "Obtain two files, read them, and produce a summary.",
            Goal = "Return a summary of both files.",
            Deliverables = ["summary of two files"],
            Constraints = ["exactly two files"],
            AcquisitionNeeds = ["download both files"],
            ReasoningNeeds = ["summarize combined content"],
            SuggestedPlanOutline = ["download files", "summarize content"]
        };

        var plannerInput = TwoPhasePlanner.BuildPlannerInput(userQuery, analysis);

        Assert.Contains("Original user request:", plannerInput, StringComparison.Ordinal);
        Assert.Contains(userQuery, plannerInput, StringComparison.Ordinal);
        Assert.Contains("internal planning analysis", plannerInput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(analysis.RewrittenRequest, plannerInput, StringComparison.Ordinal);
        Assert.Contains("does not add new external facts or new user requirements", plannerInput, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingPlanningRequestAnalyzer(PlanningRequestAnalysis analysis) : IPlanningRequestAnalyzer
    {
        public int CallCount { get; private set; }

        public string? LastUserQuery { get; private set; }

        public Task<PlanningRequestAnalysis> AnalyzeAsync(
            string userQuery,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastUserQuery = userQuery;
            return Task.FromResult(analysis);
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
