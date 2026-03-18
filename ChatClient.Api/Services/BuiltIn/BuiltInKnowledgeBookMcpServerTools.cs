using System.ComponentModel;
using System.Text.Json;
using ChatClient.Domain.Models;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

namespace ChatClient.Api.Services.BuiltIn;

[McpServerToolType]
public sealed class BuiltInKnowledgeBookMcpServerTools
{
    public static IBuiltInMcpServerDescriptor Descriptor { get; } = new BuiltInMcpServerDescriptor(
        id: Guid.Parse("da46c3f1-6bc6-4f0b-bd7b-6176daf6f6d8"),
        key: "built-in-knowledge-book",
        name: "Built-in Knowledge Book MCP Server",
        description: "Stores and navigates hierarchical knowledge in a book-like outline backed by one sandboxed knowledge file.",
        registerTools: static builder => builder.WithTools<BuiltInKnowledgeBookMcpServerTools>(),
        overrideDefinitions:
        [
            new McpOverrideDefinition
            {
                Key = KnowledgeBookStore.KnowledgeFileParameter,
                Label = "Knowledge File",
                Description = "Absolute or relative path to the JSON knowledge file used by this MCP attachment.",
                Kind = "string",
                Required = false,
                Secret = false
            }
        ]);

    [McpServerTool(Name = "kb_get_context", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns the resolved knowledge file path for this MCP session.")]
    public static object GetContext(KnowledgeBookStore store)
    {
        return store.GetContext();
    }

    [McpServerTool(Name = "kb_list_headings", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists headings under the specified outline reference. Use 0 to get top-level headings. Pass 1, 1.2, 1.2.3, etc. to inspect child sections.")]
    public static async Task<object> ListHeadingsAsync(
        KnowledgeBookStore store,
        [Description("Outline reference of the parent section. Use 0 for the root book node. Example: 1.2")] string outline = "0",
        [Description("How many heading levels to include below the selected outline.")] int maxDepth = 1,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var headings = await store.ListHeadingsAsync(outline, maxDepth, cancellationToken);
            return new
            {
                outline = string.IsNullOrWhiteSpace(outline) ? "0" : outline.Trim(),
                maxDepth = Math.Max(1, maxDepth),
                headings
            };
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new
            {
                outline,
                maxDepth
            });
        }
    }

    [McpServerTool(Name = "kb_get_section", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns one knowledge-book section with its markdown content and direct children by outline reference.")]
    public static async Task<object> GetSectionAsync(
        KnowledgeBookStore store,
        [Description("Outline reference of the section. Use 0 for the root book node. Example: 1.2.3")] string outline = "0",
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.GetSectionAsync(outline, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new
            {
                outline
            });
        }
    }

    [McpServerTool(Name = "kb_insert_section", UseStructuredContent = true)]
    [Description("Inserts a new section. Leave anchorOutline empty to append at the top level. Use a real outline like 2.3 to insert after that section on the same level. Set asChild=true to append under that section. Use virtual outlines 0 or 1.2.0 with asChild=false to insert at the start of a level.")]
    public static async Task<object> InsertSectionAsync(
        KnowledgeBookStore store,
        [Description("Title of the new section.")] string title,
        [Description("Anchor outline that defines where to insert. Leave empty to append at the top level. Use 0 for the start of the top level, 1.2 for after section 1.2, or 1.2.0 for the start of children under 1.2.")] string? anchorOutline = null,
        [Description("When true, append the new section as the last child of anchorOutline. When false, insert after anchorOutline on the same level, or at the level start for 0 / 1.2.0.")] bool asChild = false,
        [Description("Markdown content to store in the target section.")] string? contentMarkdown = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.InsertSectionAsync(title, anchorOutline, asChild, contentMarkdown, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new
            {
                title,
                anchorOutline,
                asChild
            });
        }
    }

    [McpServerTool(Name = "kb_update_section", UseStructuredContent = true)]
    [Description("Replaces the markdown content of an existing section by outline.")]
    public static async Task<object> UpdateSectionAsync(
        KnowledgeBookStore store,
        [Description("Outline reference of the section to update. Example: 1.2.3")] string outline,
        [Description("Markdown content to store in the target section. Pass an empty string to clear it.")] string contentMarkdown,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.UpdateSectionAsync(outline, contentMarkdown, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new
            {
                outline
            });
        }
    }

    [McpServerTool(Name = "kb_search_sections", ReadOnly = true, UseStructuredContent = true)]
    [Description("Searches section titles and content and returns the best matching sections.")]
    public static async Task<object> SearchSectionsAsync(
        KnowledgeBookStore store,
        [Description("Search query.")] string query,
        [Description("Maximum number of hits to return.")] int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        var hits = await store.SearchSectionsAsync(query, maxResults, cancellationToken);
        return new
        {
            query,
            hits
        };
    }

    [McpServerTool(Name = "kb_export_markdown", ReadOnly = true)]
    [Description("Exports the whole knowledge book as markdown.")]
    public static async Task<string> ExportMarkdownAsync(
        KnowledgeBookStore store,
        CancellationToken cancellationToken = default)
    {
        return await store.ExportMarkdownAsync(cancellationToken);
    }

    private static CallToolResult CreateKnownError(string code, object? details)
    {
        var message = code switch
        {
            "invalid_outline_reference" => "Invalid outline reference. Use 0 for root or dot-separated positive numbers like 1.2.3.",
            "invalid_insert_anchor_reference" => "Invalid insert anchor. Leave it empty for top-level append, use 2.3 to insert after section 2.3, or use 1.2.0 for the start of children under 1.2.",
            "section_not_found" => "Section not found for the specified outline reference.",
            "title_required" => "Provide a non-empty title for the new section.",
            "virtual_anchor_requires_same_level" => "Virtual anchors like 0 or 1.2.0 can only be used with asChild=false.",
            _ => $"Knowledge book operation failed: {code}"
        };

        return new CallToolResult
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = message
                }
            ],
            StructuredContent = JsonSerializer.SerializeToNode(new
            {
                code,
                message,
                details
            })
        };
    }
}
