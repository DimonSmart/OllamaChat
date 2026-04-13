using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace ChatClient.Api.Services.BuiltIn;

[McpServerToolType]
public sealed class BuiltInDocumentIntakeMcpServerTools
{
    public static IBuiltInMcpServerDescriptor Descriptor { get; } = new BuiltInMcpServerDescriptor(
        id: Guid.Parse("7b0ff0cd-7f37-44d0-badf-7c21a6170b51"),
        key: "built-in-document-intake",
        name: "Built-in Document Intake MCP Server",
        description: "Reads and normalizes markdown documents for later storage or workflow use. Current slice assumes the source document is already markdown.",
        registerTools: static builder => builder.WithTools<BuiltInDocumentIntakeMcpServerTools>());

    [McpServerTool(Name = "docintake_read_document", ReadOnly = true, UseStructuredContent = true)]
    [Description("Reads one document from a markdown-compatible source file and returns normalized markdown plus metadata.")]
    public static async Task<object> ReadDocumentAsync(
        MarkdownDocumentIntakeService intakeService,
        [Description("Absolute or relative path to a markdown document (.md, .markdown, .txt).")] string sourceFile,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await intakeService.ReadDocumentAsync(sourceFile, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new { sourceFile });
        }
    }

    [McpServerTool(Name = "docintake_prepare_markdown", ReadOnly = true, UseStructuredContent = true)]
    [Description("Normalizes raw markdown content and returns metadata such as title, line count, and word count.")]
    public static object PrepareMarkdown(
        MarkdownDocumentIntakeService intakeService,
        [Description("Markdown content to normalize.")] string markdown,
        [Description("Optional fallback title to use when the markdown has no heading.")] string? fallbackTitle = null)
    {
        try
        {
            return intakeService.PrepareMarkdown(markdown, fallbackTitle);
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new { fallbackTitle });
        }
    }

    private static CallToolResult CreateKnownError(string code, object? details)
    {
        var message = code switch
        {
            "source_file_required" => "Provide a sourceFile pointing to a markdown document.",
            "source_file_not_found" => "The configured sourceFile was not found.",
            "unsupported_source_format" => "Only markdown-like inputs are supported in this slice. Use .md, .markdown, .mdown, .mkd, or .txt.",
            "markdown_required" => "Provide non-empty markdown content.",
            _ => $"Document intake failed: {code}"
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
