using ChatClient.Api.Client.Components.Planning;
using ChatClient.Api.PlanningRuntime.Planning;
using System.Text.Json;

namespace ChatClient.Tests;

public class PlanningPresentationTests
{
    [Fact]
    public void GetCompactToolName_StripsBindingQualifier()
    {
        var compactName = PlanningStepPresentation.GetCompactToolName(
            "binding:ec08a4aa40aa4cc2bced12505bf3702a:search");

        Assert.Equal("search", compactName);
    }

    [Fact]
    public void GetBindingSummary_AppendsArraySuffix_ForMappedArrayReference()
    {
        var match = CreateMatch(reference: "$s1.results", mode: "map");
        var resolvedValue = JsonSerializer.SerializeToElement(new[] { "a", "b" });

        var summary = PlanningLinkPresentation.GetBindingSummary(match, resolvedValue);

        Assert.Equal("results[] -> page", summary);
    }

    [Fact]
    public void GetShapeLabel_UsesResolvedObjectKind()
    {
        var match = CreateMatch(reference: "$s1.result", mode: "value");
        var resolvedValue = JsonSerializer.SerializeToElement(new { url = "https://example.com" });

        var shape = PlanningLinkPresentation.GetShapeLabel(match, resolvedValue);

        Assert.Equal("object", shape);
    }

    private static PlanningGraphLinkMatch CreateMatch(string reference, string mode) =>
        new()
        {
            InputName = "page",
            Path = "page",
            Reference = reference,
            Mode = mode,
            DeclaredType = null,
            BindingJson = "{}",
            ReferenceJson = "{}"
        };
}
