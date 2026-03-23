using ChatClient.Api.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public class McpBindingPresentationTests
{
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
