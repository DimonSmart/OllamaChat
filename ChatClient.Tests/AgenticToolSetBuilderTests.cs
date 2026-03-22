using System.Text.Json;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services;

namespace ChatClient.Tests;

public class AgenticToolSetBuilderTests
{
    [Fact]
    public void Build_QualifiedSelection_DoesNotBroadenToSameNamedTools()
    {
        var tools = new[]
        {
            CreateTool("server-a", "search"),
            CreateTool("server-b", "search")
        };

        var toolSet = AgenticToolSetBuilder.Build(
            ["server-a:search"],
            tools);

        var registered = Assert.Single(toolSet.MetadataByName);
        Assert.Equal("search", registered.Key);
        Assert.Equal("server-a", registered.Value.ServerName);
        Assert.Equal("search", registered.Value.ToolName);
    }

    [Fact]
    public void Build_UniqueTool_KeepsOriginalToolName()
    {
        var tools = new[]
        {
            CreateTool("docs", "get_page")
        };

        var toolSet = AgenticToolSetBuilder.Build(
            ["get_page"],
            tools);

        var registered = Assert.Single(toolSet.MetadataByName);
        Assert.Equal("get_page", registered.Key);
        Assert.Equal("docs", registered.Value.ServerName);
    }

    [Fact]
    public void Build_ShortSelectionWithNameCollision_DisambiguatesRegisteredNames()
    {
        var tools = new[]
        {
            CreateTool("server-a", "search", bindingDisplayName: "Docs A"),
            CreateTool("server-b", "search", bindingDisplayName: "Docs B")
        };

        var toolSet = AgenticToolSetBuilder.Build(
            ["search"],
            tools);

        Assert.Equal(2, toolSet.MetadataByName.Count);
        Assert.Contains("Docs_A__search", toolSet.MetadataByName.Keys);
        Assert.Contains("Docs_B__search", toolSet.MetadataByName.Keys);
    }

    private static AppToolDescriptor CreateTool(
        string serverName,
        string toolName,
        string? bindingDisplayName = null)
    {
        return new AppToolDescriptor(
            QualifiedName: $"{serverName}:{toolName}",
            ServerName: serverName,
            ToolName: toolName,
            DisplayName: toolName,
            Description: $"{toolName} tool from {serverName}",
            InputSchema: CreateSchema(),
            OutputSchema: null,
            MayRequireUserInput: false,
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            OpenWorldHint: false,
            ExecuteAsync: static (_, _) => Task.FromResult<object>("ok"),
            BaseQualifiedName: $"{serverName}:{toolName}",
            BaseServerName: serverName,
            BindingId: null,
            BindingDisplayName: bindingDisplayName);
    }

    private static JsonElement CreateSchema()
    {
        using var document = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        return document.RootElement.Clone();
    }
}
