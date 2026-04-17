using ChatClient.Api.Client.Components.Planning;
using System.Text.Json;

namespace ChatClient.Tests;

public sealed class PlanningFinalResultPresentationTests
{
    [Fact]
    public void TryExtractUserFacingAnswer_ReturnsNamedMarkdownField()
    {
        var result = JsonSerializer.SerializeToElement(new
        {
            userFacingAnswer = "# Final answer\n\n- item one\n- item two",
            debug = new { steps = 3 }
        });

        var extracted = PlanningFinalResultPresentation.TryExtractUserFacingAnswer(result, out var markdown);

        Assert.True(extracted);
        Assert.Equal("# Final answer\n\n- item one\n- item two", markdown);
    }

    [Fact]
    public void TryExtractSummary_PrefersUserFacingAnswer_WhenPresent()
    {
        var result = JsonSerializer.SerializeToElement(new
        {
            summary = "Short fallback",
            userFacingAnswer = "# Final answer\n\nRendered for the user."
        });

        var extracted = PlanningFinalResultPresentation.TryExtractSummary(result, out var summary);

        Assert.True(extracted);
        Assert.Equal("# Final answer\n\nRendered for the user.", summary);
    }
}
