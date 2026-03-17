using ChatClient.Api.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public class McpServerSessionBindingMergerTests
{
    [Fact]
    public void Merge_SessionBindingOverridesRootsAndSpecificParameters()
    {
        var agentBinding = new McpServerSessionBinding
        {
            ServerName = "Built-in File Sandbox MCP Server",
            Roots = ["C:\\agent-root"],
            Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["glob"] = "*.md",
                ["mode"] = "agent-default"
            }
        };

        var sessionBinding = new McpServerSessionBinding
        {
            ServerName = "Built-in File Sandbox MCP Server",
            Roots = ["D:\\session-root"],
            Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["glob"] = "*.txt"
            }
        };

        var merged = McpServerSessionBindingMerger.Merge([agentBinding], [sessionBinding]);

        var binding = Assert.Single(merged);
        Assert.Equal("Built-in File Sandbox MCP Server", binding.ServerName);
        Assert.Equal(["D:\\session-root"], binding.Roots);
        Assert.Equal("*.txt", binding.Parameters["glob"]);
        Assert.Equal("agent-default", binding.Parameters["mode"]);
    }
}
