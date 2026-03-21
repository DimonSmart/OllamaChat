using System.ComponentModel;
using System.Text.Json;
using ChatClient.Domain.Models;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ChatClient.Api.Services.BuiltIn;

[McpServerToolType]
public sealed class BuiltInMarkdownDocumentMcpServerTools
{
    public static IBuiltInMcpServerDescriptor Descriptor { get; } = new BuiltInMcpServerDescriptor(
        id: Guid.Parse("6d2ce2af-b2eb-4c52-b4ef-8df1bbcaf7c2"),
        key: "built-in-markdown-document",
        name: "Built-in Markdown Document MCP Server",
        description: "Reads and updates one markdown document bound to the MCP session through a Markdig-based document model.",
        registerTools: static builder => builder.WithTools<BuiltInMarkdownDocumentMcpServerTools>(),
        overrideDefinitions:
        [
            new McpOverrideDefinition
            {
                Key = MarkdownDocumentSession.SourceFileParameter,
                Label = "Source File",
                Description = "Absolute or relative path to the markdown document used by this MCP attachment.",
                Kind = "string",
                Required = true,
                Secret = false
            }
        ]);

    [McpServerTool(Name = "doc_get_context", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns the resolved markdown source file path and document identity for this MCP session.")]
    public static object GetContext(MarkdownDocumentSession session)
    {
        return session.GetContext();
    }

    [McpServerTool(Name = "doc_list_headings", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists headings under the specified outline reference. Use 0 for document root. Pass 1, 1.2, 1.2.3, etc. to inspect child sections.")]
    public static async Task<object> ListHeadingsAsync(
        MarkdownDocumentSession session,
        [Description("Outline reference of the parent section. Use 0 for the root document node. Example: 1.2")] string outline = "0",
        [Description("How many heading levels to include below the selected outline.")] int maxDepth = 1,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var headings = await session.ListHeadingsAsync(outline, maxDepth, cancellationToken);
            return new
            {
                outline = string.IsNullOrWhiteSpace(outline) ? MarkdownDocumentSession.RootOutlineReference : outline.Trim(),
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

    [McpServerTool(Name = "doc_get_section", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns one markdown section with its local content and direct child headings by outline reference.")]
    public static async Task<object> GetSectionAsync(
        MarkdownDocumentSession session,
        [Description("Outline reference of the section. Use 0 for the root document node. Example: 1.2.3")] string outline = "0",
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await session.GetSectionAsync(outline, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new
            {
                outline
            });
        }
    }

    [McpServerTool(Name = "doc_list_items", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists markdown document items with semantic pointers. Useful for cursor planning, targeted edits, and chunked reading.")]
    public static async Task<object> ListItemsAsync(
        MarkdownDocumentSession session,
        [Description("Outline reference to scope the item stream. Use 0 for the whole document.")] string outline = "0",
        [Description("Resume after this semantic pointer. Optional.")] string? startAfterPointer = null,
        [Description("Maximum number of items to return.")] int maxItems = 20,
        [Description("Whether heading items should be included.")] bool includeHeadings = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await session.ListItemsAsync(outline, startAfterPointer, maxItems, includeHeadings, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new
            {
                outline,
                startAfterPointer,
                maxItems,
                includeHeadings
            });
        }
    }

    [McpServerTool(Name = "doc_search_sections", ReadOnly = true, UseStructuredContent = true)]
    [Description("Searches markdown section titles and content and returns the best matching sections.")]
    public static async Task<object> SearchSectionsAsync(
        MarkdownDocumentSession session,
        [Description("Search query.")] string query,
        [Description("Maximum number of hits to return.")] int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        var hits = await session.SearchSectionsAsync(query, maxResults, cancellationToken);
        return new
        {
            query,
            hits
        };
    }

    [McpServerTool(Name = "doc_apply_operations", UseStructuredContent = true)]
    [Description("Applies ordered markdown edit operations to the bound source file using semantic pointers or item indices.")]
    public static async Task<object> ApplyOperationsAsync(
        MarkdownDocumentSession session,
        [Description("Ordered markdown edit operations to apply to the bound source file.")] IReadOnlyList<MarkdownDocumentEditOperationInput> operations,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await session.ApplyOperationsAsync(new MarkdownDocumentApplyOperationsInput(operations), cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new { operations });
        }
    }

    [McpServerTool(Name = "doc_export_markdown", ReadOnly = true)]
    [Description("Exports the whole bound markdown document as raw markdown.")]
    public static async Task<string> ExportMarkdownAsync(
        MarkdownDocumentSession session,
        CancellationToken cancellationToken = default)
    {
        return await session.ExportMarkdownAsync(cancellationToken);
    }

    private static CallToolResult CreateKnownError(string code, object? details)
    {
        var message = code switch
        {
            "source_file_required" => "Provide a sourceFile override for this markdown document MCP attachment.",
            "source_file_not_found" => "The configured sourceFile was not found.",
            "invalid_outline_reference" => "Invalid outline reference. Use 0 for root or dot-separated positive numbers like 1.2.3.",
            "section_not_found" => "Section not found for the specified outline reference.",
            "invalid_pointer" => "Invalid semantic pointer. Use labels like 1.2 or 1.2.p3.",
            "pointer_not_found" => "The requested semantic pointer does not exist in the current document.",
            "edit_operations_required" => "Provide at least one edit operation.",
            "invalid_edit_action" => "Invalid edit action. Use replace, insert_before, insert_after, remove, split, merge_with_next, or merge_with_previous.",
            "edit_target_required" => "Each edit operation must define targetPointer or targetIndex.",
            "target_index_out_of_range" => "The requested targetIndex is outside the current document range.",
            "edit_items_required" => "This edit action requires one or more replacement markdown items.",
            "invalid_edit_item_markdown" => "Each edit item must contain exactly one valid markdown block.",
            "merge_target_missing" => "Cannot merge because the neighboring item does not exist.",
            _ => $"Markdown document operation failed: {code}"
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
