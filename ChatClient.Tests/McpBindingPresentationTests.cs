using ChatClient.Api.Services;
using ChatClient.Domain.Models;
using ModelContextProtocol.Client;

namespace ChatClient.Tests;

public class McpBindingPresentationTests
{
    [Fact]
    public void GetBindingLabel_UsesSourceFileTail_WhenDisplayNameMissing()
    {
        var binding = new McpServerSessionBinding
        {
            BindingId = Guid.NewGuid(),
            Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceFile"] = @"C:\Books\Neznaika\book.md"
            }
        };

        var label = McpBindingPresentation.GetBindingLabel(binding);

        Assert.Equal(@"...\Neznaika\book.md", label);
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

    [Fact]
    public void GetServerDisplayName_UsesExplicitDisplayName_OverInferredLabel()
    {
        var descriptor = new McpServerConfig
        {
            Name = "Built-in Markdown Document MCP Server"
        };
        var binding = new McpServerSessionBinding
        {
            DisplayName = "Character dossier",
            Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceFile"] = @"C:\Books\Neznaika\dossier.md"
            }
        };

        var displayName = McpBindingPresentation.GetServerDisplayName(descriptor, binding);

        Assert.Equal("Built-in Markdown Document MCP Server / Character dossier", displayName);
    }

    [Fact]
    public void McpClientHandle_DisplayName_UsesInferredBindingLabel()
    {
        var descriptor = new McpServerConfig
        {
            Name = "Built-in Markdown Document MCP Server"
        };
        var binding = new McpServerSessionBinding
        {
            BindingId = Guid.NewGuid(),
            Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceFile"] = @"C:\Books\Neznaika\book.md"
            }
        };

        var handle = new McpClientHandle(
            Client: null!,
            ServerDescriptor: descriptor,
            Binding: binding);

        Assert.Equal("Built-in Markdown Document MCP Server / ...\\Neznaika\\book.md", handle.DisplayName);
        Assert.Equal("...\\Neznaika\\book.md", handle.BindingDisplayName);
    }
}
