using ChatClient.Domain.Models;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace ChatClient.Api.Services.BuiltIn;

[McpServerToolType]
public sealed class BuiltInTaskSessionMcpServerTools
{
    public static IBuiltInMcpServerDescriptor Descriptor { get; } = new BuiltInMcpServerDescriptor(
        id: Guid.Parse("c6f1b7d3-f90b-4dc7-b416-4435af0c1b82"),
        key: "built-in-workflow-state",
        name: "Built-in Workflow State MCP Server",
        description: "Provides workflow inputs, documents, parameters, phase, and named outputs without duplicating conversation history.",
        registerTools: static builder => builder.WithTools<BuiltInTaskSessionMcpServerTools>(),
        overrideDefinitions:
        [
            new McpOverrideDefinition
            {
                Key = TaskSessionStore.DatabaseFileParameter,
                Label = "Database File",
                Description = "Absolute or relative path to the SQLite database used by this workflow-state attachment.",
                Kind = "string",
                Required = false,
                Secret = false
            },
            new McpOverrideDefinition
            {
                Key = TaskSessionStore.SessionIdParameter,
                Label = "Session Id",
                Description = "Optional default workflow-run id. When configured, session tools can omit the sessionId argument.",
                Kind = "string",
                Required = false,
                Secret = false
            }
        ]);

    [McpServerTool(Name = "session_get", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns the current workflow-run snapshot including phase and document, parameter, and output inventories.")]
    public static async Task<object> GetSessionAsync(
        TaskSessionStore store,
        [Description("Workflow-run id. Omit when the MCP binding defines the sessionId parameter.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.GetSessionAsync(sessionId, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new { sessionId });
        }
    }

    [McpServerTool(Name = "session_set_phase", UseStructuredContent = true)]
    [Description("Updates the current workflow-run phase label.")]
    public static async Task<object> SetPhaseAsync(
        TaskSessionStore store,
        [Description("New workflow phase label, for example intake, behavioural, technical, summary.")] string phase,
        [Description("Workflow-run id. Omit when the MCP binding defines the sessionId parameter.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.SetPhaseAsync(sessionId, phase, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new { sessionId, phase });
        }
    }

    [McpServerTool(Name = "session_get_document", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns one launch document stored under the specified semantic kind for the workflow run.")]
    public static async Task<object> GetDocumentAsync(
        TaskSessionStore store,
        [Description("Semantic kind of the document to retrieve.")] string kind,
        [Description("Workflow-run id. Omit when the MCP binding defines the sessionId parameter.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.GetDocumentAsync(sessionId, kind, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new { sessionId, kind });
        }
    }

    [McpServerTool(Name = "session_get_parameter", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns one launch parameter by key from the workflow run.")]
    public static async Task<object> GetParameterAsync(
        TaskSessionStore store,
        [Description("Parameter key to retrieve.")] string key,
        [Description("Workflow-run id. Omit when the MCP binding defines the sessionId parameter.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.GetParameterAsync(sessionId, key, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new { sessionId, key });
        }
    }

    [McpServerTool(Name = "session_save_summary", UseStructuredContent = true)]
    [Description("Stores or replaces one named Markdown output for the workflow run, for example final, verdict, or review.")]
    public static async Task<object> SaveSummaryAsync(
        TaskSessionStore store,
        [Description("Summary label, for example final.")] string label,
        [Description("Markdown summary content to store.")] string markdown,
        [Description("Workflow-run id. Omit when the MCP binding defines the sessionId parameter.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.SaveSummaryAsync(sessionId, label, markdown, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new { sessionId, label });
        }
    }

    private static CallToolResult CreateKnownError(string code, object? details)
    {
        var message = code switch
        {
            "session_id_required" => "Provide a workflow-run sessionId or bind one to this MCP connection.",
            "session_not_found" => "The requested workflow run does not exist.",
            "phase_required" => "Provide a non-empty phase label.",
            "document_kind_required" => "Provide a semantic document kind.",
            "document_markdown_required" => "Provide non-empty markdown content for the document.",
            "document_not_found" => "The requested document was not found in the workflow run.",
            "parameter_key_required" => "Provide a non-empty parameter key.",
            "parameter_value_kind_required" => "Provide a non-empty parameter value kind.",
            "parameter_value_required" => "Provide a non-empty parameter value.",
            "parameter_not_found" => "The requested parameter was not found in the workflow run.",
            "summary_label_required" => "Provide a non-empty summary label.",
            "summary_markdown_required" => "Provide non-empty markdown content for the summary.",
            _ => $"Workflow-state operation failed: {code}"
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
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                code,
                message,
                details
            })
        };
    }
}
