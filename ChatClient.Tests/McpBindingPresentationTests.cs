using ChatClient.Api.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public class McpBindingPresentationTests
{
    [Theory]
    [InlineData(false, true, 0, "Off")]
    [InlineData(true, true, 0, "All tools")]
    [InlineData(true, false, 0, "No tools")]
    [InlineData(true, false, 1, "1 tool")]
    [InlineData(true, false, 3, "3 tools")]
    public void GetCompactToolState_FormatsSemanticState(
        bool enabled,
        bool selectAllTools,
        int selectedToolCount,
        string expected)
    {
        var binding = new McpServerSessionBinding
        {
            Enabled = enabled,
            SelectAllTools = selectAllTools,
            SelectedTools = Enumerable.Range(1, selectedToolCount)
                .Select(index => $"tool-{index}")
                .ToList()
        };

        Assert.Equal(expected, McpBindingPresentation.GetCompactToolState(binding));
    }

    [Fact]
    public void BuildToolDescription_AppendsBindingContext()
    {
        var binding = new McpServerSessionBinding
        {
            Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceFile"] = @"C:\Books\Neznaika\book.md"
            }
        };

        var description = McpBindingPresentation.BuildToolDescription(
            "Reads and updates the bound markdown document.",
            binding);

        Assert.Contains("Reads and updates the bound markdown document.", description, StringComparison.Ordinal);
        Assert.Contains("Binding context:", description, StringComparison.Ordinal);
        Assert.Contains(@"sourceFile=...\Neznaika\book.md", description, StringComparison.Ordinal);
    }
}
