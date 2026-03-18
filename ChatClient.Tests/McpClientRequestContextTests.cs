using ChatClient.Api.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public class McpClientRequestContextTests
{
    [Fact]
    public void FindBindingsFor_SameServer_ReturnsAllConfiguredInstances()
    {
        var serverId = Guid.NewGuid();
        var descriptor = new McpServerConfig
        {
            Id = serverId,
            Name = "Built-in Knowledge Book MCP Server"
        };

        var context = new McpClientRequestContext(
        [
            new McpServerSessionBinding
            {
                BindingId = Guid.NewGuid(),
                ServerId = serverId,
                ServerName = descriptor.Name,
                DisplayName = "One"
            },
            new McpServerSessionBinding
            {
                BindingId = Guid.NewGuid(),
                ServerId = serverId,
                ServerName = descriptor.Name,
                DisplayName = "Two"
            }
        ]);

        var bindings = context.FindBindingsFor(descriptor);

        Assert.Equal(2, bindings.Count);
        Assert.Contains(bindings, binding => binding.DisplayName == "One");
        Assert.Contains(bindings, binding => binding.DisplayName == "Two");
    }

    [Fact]
    public void BuildFingerprint_DoesNotExposeRawRootsOrParameterValues()
    {
        const string rootPath = "C:\\secret-root";
        const string connectionString = "Server=prod;Password=super-secret;";

        var context = new McpClientRequestContext(
        [
            new McpServerSessionBinding
            {
                BindingId = Guid.NewGuid(),
                ServerName = "SQL MCP",
                Roots = [rootPath],
                Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["connectionString"] = connectionString
                }
            }
        ]);

        var fingerprint = context.BuildFingerprint();

        Assert.DoesNotContain(rootPath, fingerprint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(connectionString, fingerprint, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^[0-9A-F]+$", fingerprint);
    }
}
