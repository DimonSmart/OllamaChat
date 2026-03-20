using System.Text.Json;
using ChatClient.Api.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Tests;

public class McpBindingToolSelectionResolverTests
{
    [Fact]
    public void FilterAvailableTools_NoBindings_ReturnsEntireCatalog()
    {
        var tools = new[]
        {
            CreateTool("web", "search"),
            CreateTool("knowledge", "get_section")
        };

        var filtered = McpBindingToolSelectionResolver.FilterAvailableTools([], tools);

        Assert.Equal(2, filtered.Count);
        Assert.Contains(filtered, tool => tool.QualifiedName == "knowledge:get_section");
        Assert.Contains(filtered, tool => tool.QualifiedName == "web:search");
    }

    [Fact]
    public void FilterAvailableTools_SelectAllDisabledWithoutSelectedTools_ReturnsNoTools()
    {
        var bindingId = Guid.NewGuid();
        var binding = new McpServerSessionBinding
        {
            BindingId = bindingId,
            ServerName = "Built-in Knowledge Book MCP Server",
            Enabled = true,
            SelectAllTools = false
        };

        var tools = new[]
        {
            CreateTool(
                serverName: "Knowledge / Characters",
                toolName: "kb_get_section",
                bindingId: bindingId,
                baseServerName: "Built-in Knowledge Book MCP Server")
        };

        var filtered = McpBindingToolSelectionResolver.FilterAvailableTools([binding], tools);

        Assert.Empty(filtered);
    }

    [Fact]
    public void ResolveQualifiedToolNames_UsesBindingIdToSeparateAliases()
    {
        var readBindingId = Guid.NewGuid();
        var dossierBindingId = Guid.NewGuid();
        var binding = new McpServerSessionBinding
        {
            BindingId = dossierBindingId,
            ServerName = "Built-in Knowledge Book MCP Server",
            Enabled = true,
            SelectAllTools = false,
            SelectedTools = ["kb_upsert_note"]
        };

        var tools = new[]
        {
            CreateTool(
                serverName: "Knowledge / Reader",
                toolName: "kb_upsert_note",
                bindingId: readBindingId,
                baseServerName: "Built-in Knowledge Book MCP Server"),
            CreateTool(
                serverName: "Knowledge / Dossier",
                toolName: "kb_upsert_note",
                bindingId: dossierBindingId,
                baseServerName: "Built-in Knowledge Book MCP Server")
        };

        var resolved = McpBindingToolSelectionResolver.ResolveQualifiedToolNames([binding], tools);

        var qualifiedName = Assert.Single(resolved);
        Assert.Equal($"binding:{dossierBindingId:N}:kb_upsert_note", qualifiedName);
    }

    private static AppToolDescriptor CreateTool(
        string serverName,
        string toolName,
        Guid? bindingId = null,
        string? baseServerName = null)
    {
        var qualifiedName = bindingId is Guid resolvedBindingId && resolvedBindingId != Guid.Empty
            ? $"binding:{resolvedBindingId:N}:{toolName}"
            : $"{(baseServerName ?? serverName)}:{toolName}";

        return new AppToolDescriptor(
            QualifiedName: qualifiedName,
            ServerName: serverName,
            ToolName: toolName,
            DisplayName: toolName,
            Description: string.Empty,
            InputSchema: CreateSchema(),
            OutputSchema: null,
            MayRequireUserInput: false,
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            OpenWorldHint: false,
            ExecuteAsync: static (_, _) => Task.FromResult<object>(string.Empty),
            BaseQualifiedName: $"{(baseServerName ?? serverName)}:{toolName}",
            BaseServerName: baseServerName ?? serverName,
            BindingId: bindingId,
            BindingDisplayName: null);
    }

    private static JsonElement CreateSchema()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
