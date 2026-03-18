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

    [Fact]
    public void Merge_DifferentBindingIdsForSameServer_PreservesBothInstances()
    {
        var serverId = Guid.NewGuid();
        var firstBindingId = Guid.NewGuid();
        var secondBindingId = Guid.NewGuid();

        var agentBinding = new McpServerSessionBinding
        {
            BindingId = firstBindingId,
            ServerId = serverId,
            ServerName = "Built-in Knowledge Book MCP Server",
            DisplayName = "Base",
            Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["knowledgeFile"] = "C:\\kb\\one.json"
            }
        };

        var sessionBinding = new McpServerSessionBinding
        {
            BindingId = secondBindingId,
            ServerId = serverId,
            ServerName = "Built-in Knowledge Book MCP Server",
            DisplayName = "Extra",
            Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["knowledgeFile"] = "C:\\kb\\two.json"
            }
        };

        var merged = McpServerSessionBindingMerger.Merge([agentBinding], [sessionBinding]);

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, binding => binding.BindingId == firstBindingId && binding.DisplayName == "Base");
        Assert.Contains(merged, binding => binding.BindingId == secondBindingId && binding.DisplayName == "Extra");
    }

    [Fact]
    public void Merge_SameBindingId_OverridesExistingBindingInPlace()
    {
        var bindingId = Guid.NewGuid();

        var agentBinding = new McpServerSessionBinding
        {
            BindingId = bindingId,
            ServerName = "Built-in File Sandbox MCP Server",
            DisplayName = "Workspace",
            SelectAllTools = true,
            Roots = ["C:\\agent-root"],
            Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["glob"] = "*.md"
            }
        };

        var sessionBinding = new McpServerSessionBinding
        {
            BindingId = bindingId,
            ServerName = "Built-in File Sandbox MCP Server",
            DisplayName = "Workspace Override",
            SelectAllTools = false,
            SelectedTools = ["fs_read"],
            Roots = ["D:\\session-root"],
            Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["glob"] = "*.cs"
            }
        };

        var merged = McpServerSessionBindingMerger.Merge([agentBinding], [sessionBinding]);

        var binding = Assert.Single(merged);
        Assert.Equal(bindingId, binding.BindingId);
        Assert.Equal("Workspace Override", binding.DisplayName);
        Assert.False(binding.SelectAllTools);
        Assert.Equal(["fs_read"], binding.SelectedTools);
        Assert.Equal(["D:\\session-root"], binding.Roots);
        Assert.Equal("*.cs", binding.Parameters["glob"]);
    }
}
