using System.Text.Json.Nodes;
using ChatClient.Api.Services;

namespace ChatClient.Tests;

public sealed class PlanningWorkflowExperimentPlanWorkspaceTests
{
    [Fact]
    public void PrepareDownloadInputs_WithoutExplicitSource_AggregatesTerminalSearchBranches()
    {
        var workspace = CreateWorkspace();

        workspace.SetGoal("Find products.");
        workspace.AddSearchStep(query: "robot vacuum mop under 600 eur", stepId: "s1");
        workspace.AddSearchStep(afterStepId: "s1", query: "best robot vacuum mop 600 eur", stepId: "s2");
        workspace.AddPrepareDownloadInputsStep(afterStepId: "s2", preparationGoal: "Keep product pages.", stepId: "s3");

        var plan = workspace.BuildPlan();
        var step = plan.Steps.Single(candidate => candidate.Id == "s3");
        var records = Assert.IsType<JsonObject>(Assert.Contains("records", step.In!));
        var concat = Assert.IsType<JsonArray>(Assert.Contains("concat", records));

        Assert.Equal(2, concat.Count);
    }

    [Fact]
    public void Download_WithoutExplicitSource_RejectsAmbiguousTerminalBranches()
    {
        var workspace = CreateWorkspace();

        workspace.SetGoal("Find products.");
        workspace.AddSearchStep(query: "robot vacuum mop under 600 eur", stepId: "s1");
        workspace.AddSearchStep(afterStepId: "s1", query: "best robot vacuum mop 600 eur", stepId: "s2");

        var result = workspace.AddDownloadStep(afterStepId: "s2", stepId: "s3");

        Assert.False(result["ok"]?.GetValue<bool>() ?? true);
        Assert.Equal("download_source_ambiguous", result["error"]?["code"]?.GetValue<string>());
    }

    [Fact]
    public void Answer_WithoutExplicitSource_AggregatesTerminalReasoningBranches()
    {
        var workspace = CreateWorkspace();

        workspace.SetGoal("Recommend products.");
        workspace.AddSearchStep(query: "robot vacuum mop under 600 eur", stepId: "s1");
        workspace.AddExtractStep(afterStepId: "s1", sourceStepId: "s1", extractionGoal: "Extract candidate product records.", stepId: "s2");
        workspace.AddSearchStep(afterStepId: "s2", query: "best robot vacuum mop 600 eur", stepId: "s3");
        workspace.AddExtractStep(afterStepId: "s3", sourceStepId: "s3", extractionGoal: "Extract candidate product records.", stepId: "s4");
        workspace.AddAnswerStep(afterStepId: "s4", answerGoal: "Recommend the best options.", outputLanguage: "Russian", stepId: "s5");

        var plan = workspace.BuildPlan();
        var step = plan.Steps.Single(candidate => candidate.Id == "s5");
        var records = Assert.IsType<JsonObject>(Assert.Contains("records", step.In!));
        var concat = Assert.IsType<JsonArray>(Assert.Contains("concat", records));

        Assert.Equal(2, concat.Count);
    }

    [Fact]
    public void ValidateSemantics_Fails_WhenResultStepIsMissing()
    {
        var workspace = CreateWorkspace("Посоветуй хороший робот пылесос с мойкой до 600 EUR");

        workspace.SetGoal("Recommend robot vacuum mop models under 600 EUR.");
        workspace.AddSearchStep(query: "robot vacuum mop under 600 eur", stepId: "s1");
        workspace.AddDownloadStep(afterStepId: "s1", sourceStepId: "s1", stepId: "s2");
        workspace.AddExtractStep(afterStepId: "s2", sourceStepId: "s2", extractionGoal: "Extract candidate products.", stepId: "s3");

        var ok = workspace.TryValidateSemantics(out var issue);

        Assert.False(ok);
        Assert.Equal("result_step_missing", issue?.Code);
    }

    [Fact]
    public void ValidateSemantics_Passes_WhenMarkedResultStepExists_ForNeutralIntent()
    {
        var workspace = CreateWorkspace();

        workspace.SetGoal("Process collected records.");
        workspace.AddSearchStep(query: "robot vacuum mop under 600 eur", stepId: "s1");
        workspace.AddFilterStep(afterStepId: "s1", sourceStepId: "s1", filterGoal: "Keep useful records.", stepId: "s2");
        workspace.MarkResultStep("s2");

        var ok = workspace.TryValidateSemantics(out var issue);

        Assert.True(ok);
        Assert.Null(issue);
    }

    [Fact]
    public void ValidateSemantics_Passes_WhenWorkflowConvergesToSingleMarkedResult()
    {
        var workspace = CreateWorkspace("Посоветуй хороший робот пылесос с мойкой до 600 EUR");

        workspace.SetGoal("Recommend robot vacuum mop models under 600 EUR.");
        workspace.AddSearchStep(query: "robot vacuum mop under 600 eur", stepId: "s1");
        workspace.AddDownloadStep(afterStepId: "s1", sourceStepId: "s1", stepId: "s2");
        workspace.AddExtractStep(afterStepId: "s2", sourceStepId: "s2", extractionGoal: "Extract candidate products.", stepId: "s3");
        workspace.AddAnswerStep(afterStepId: "s3", sourceStepId: "s3", answerGoal: "Write final answer.", outputLanguage: "Russian", stepId: "s4");
        workspace.MarkResultStep("s4");

        var ok = workspace.TryValidateSemantics(out var issue);

        Assert.True(ok);
        Assert.Null(issue);
    }

    private static PlanningWorkflowExperimentPlanWorkspace CreateWorkspace(string? userQuery = null) =>
        new(Array.Empty<AppToolDescriptor>(), userQuery);
}
