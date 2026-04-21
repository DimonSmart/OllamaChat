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
        key: "built-in-task-session",
        name: "Built-in Task Session MCP Server",
        description: "Stores generic task-scoped state, documents, turns, and summaries in a SQLite-backed session store.",
        registerTools: static builder => builder.WithTools<BuiltInTaskSessionMcpServerTools>(),
        overrideDefinitions:
        [
            new McpOverrideDefinition
            {
                Key = TaskSessionStore.DatabaseFileParameter,
                Label = "Database File",
                Description = "Absolute or relative path to the SQLite database used by this task session MCP attachment.",
                Kind = "string",
                Required = false,
                Secret = false
            },
            new McpOverrideDefinition
            {
                Key = TaskSessionStore.SessionIdParameter,
                Label = "Session Id",
                Description = "Optional default task session id. When configured, session tools can omit the sessionId argument.",
                Kind = "string",
                Required = false,
                Secret = false
            }
        ]);

    [McpServerTool(Name = "session_get_context", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns the resolved SQLite database path used by this task session MCP attachment.")]
    public static object GetContext(TaskSessionStore store)
    {
        return store.GetContext();
    }

    [McpServerTool(Name = "session_create", UseStructuredContent = true)]
    [Description("Creates a new generic task session that can store documents, turns, and summaries for the current workflow.")]
    public static async Task<object> CreateSessionAsync(
        TaskSessionStore store,
        [Description("Optional human-readable title for the task session.")] string? title = null,
        [Description("Optional description of the task or scenario.")] string? description = null,
        CancellationToken cancellationToken = default)
    {
        return await store.CreateSessionAsync(title, description, cancellationToken);
    }

    [McpServerTool(Name = "session_get", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns the current session snapshot including phase, document inventory, parameter inventory, summary inventory, and turn count.")]
    public static async Task<object> GetSessionAsync(
        TaskSessionStore store,
        [Description("Task session id returned by session_create. Optional when the MCP binding already defines a sessionId parameter.")] string? sessionId = null,
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
    [Description("Updates the current workflow phase label for a task session.")]
    public static async Task<object> SetPhaseAsync(
        TaskSessionStore store,
        [Description("New workflow phase label, for example intake, behavioural, technical, summary.")] string phase,
        [Description("Task session id returned by session_create. Optional when the MCP binding already defines a sessionId parameter.")] string? sessionId = null,
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

    [McpServerTool(Name = "session_attach_document", UseStructuredContent = true)]
    [Description("Attaches or replaces one markdown document under a semantic kind within the task session, for example resume, job_description, notes, or brief.")]
    public static async Task<object> AttachDocumentAsync(
        TaskSessionStore store,
        [Description("Semantic kind of the document, for example resume, job_description, notes, or brief.")] string kind,
        [Description("Markdown content to store for the document.")] string markdown,
        [Description("Optional display title for the document.")] string? title = null,
        [Description("Optional source path or source label for the document.")] string? source = null,
        [Description("Task session id returned by session_create. Optional when the MCP binding already defines a sessionId parameter.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.AttachDocumentAsync(sessionId, kind, markdown, title, source, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new { sessionId, kind, title, source });
        }
    }

    [McpServerTool(Name = "session_get_document", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns one markdown document stored under the specified semantic kind within the task session.")]
    public static async Task<object> GetDocumentAsync(
        TaskSessionStore store,
        [Description("Semantic kind of the document to retrieve.")] string kind,
        [Description("Task session id returned by session_create. Optional when the MCP binding already defines a sessionId parameter.")] string? sessionId = null,
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

    [McpServerTool(Name = "session_set_parameter", UseStructuredContent = true)]
    [Description("Stores or replaces one named scalar or structured parameter in the task session, for example response_language, budget, seniority, or article_outline_json.")]
    public static async Task<object> SetParameterAsync(
        TaskSessionStore store,
        [Description("Parameter key to store or replace.")] string key,
        [Description("Logical value kind, for example text, number, boolean, or json.")] string valueKind,
        [Description("Parameter value stored as text. Json values should be passed as serialized JSON text.")] string value,
        [Description("Task session id returned by session_create. Optional when the MCP binding already defines a sessionId parameter.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.SetParameterAsync(sessionId, key, valueKind, value, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new { sessionId, key, valueKind });
        }
    }

    [McpServerTool(Name = "session_get_parameter", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns one stored parameter by key from the task session.")]
    public static async Task<object> GetParameterAsync(
        TaskSessionStore store,
        [Description("Parameter key to retrieve.")] string key,
        [Description("Task session id returned by session_create. Optional when the MCP binding already defines a sessionId parameter.")] string? sessionId = null,
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

    [McpServerTool(Name = "session_append_turn", UseStructuredContent = true)]
    [Description("Appends one conversational turn or workflow note to the task session transcript.")]
    public static async Task<object> AppendTurnAsync(
        TaskSessionStore store,
        [Description("Role of the participant, for example user, assistant, system, or tool.")] string role,
        [Description("Turn content to store.")] string content,
        [Description("Optional logical speaker id, such as triage, receptionist, behavioural, technical, summarizer, or user.")] string? speakerId = null,
        [Description("Task session id returned by session_create. Optional when the MCP binding already defines a sessionId parameter.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.AppendTurnAsync(sessionId, role, content, speakerId, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new { sessionId, role, speakerId });
        }
    }

    [McpServerTool(Name = "session_list_turns", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists conversation turns stored in the task session transcript.")]
    public static async Task<object> ListTurnsAsync(
        TaskSessionStore store,
        [Description("Task session id returned by session_create. Optional when the MCP binding already defines a sessionId parameter.")] string? sessionId = null,
        [Description("Only return turns with a sequence greater than this value.")] long? afterSequence = null,
        [Description("Maximum number of turns to return.")] int maxCount = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var turns = await store.ListTurnsAsync(sessionId, afterSequence, maxCount, cancellationToken);
            return new
            {
                sessionId,
                afterSequence = Math.Max(0, afterSequence ?? 0),
                maxCount = Math.Clamp(maxCount, 1, 200),
                turns
            };
        }
        catch (InvalidOperationException ex)
        {
            return CreateKnownError(ex.Message, new { sessionId, afterSequence, maxCount });
        }
    }

    [McpServerTool(Name = "session_save_summary", UseStructuredContent = true)]
    [Description("Stores or replaces one named markdown summary for the task session, for example final, draft, or reviewer_notes.")]
    public static async Task<object> SaveSummaryAsync(
        TaskSessionStore store,
        [Description("Summary label, for example final.")] string label,
        [Description("Markdown summary content to store.")] string markdown,
        [Description("Task session id returned by session_create. Optional when the MCP binding already defines a sessionId parameter.")] string? sessionId = null,
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
            "session_id_required" => "Provide a sessionId returned by session_create.",
            "session_not_found" => "The requested task session does not exist.",
            "phase_required" => "Provide a non-empty phase label.",
            "document_kind_required" => "Provide a semantic document kind.",
            "document_markdown_required" => "Provide non-empty markdown content for the document.",
            "document_not_found" => "The requested document was not found in the task session.",
            "parameter_key_required" => "Provide a non-empty parameter key.",
            "parameter_value_kind_required" => "Provide a non-empty parameter value kind.",
            "parameter_value_required" => "Provide a non-empty parameter value.",
            "parameter_not_found" => "The requested parameter was not found in the task session.",
            "turn_role_required" => "Provide a non-empty turn role.",
            "turn_content_required" => "Provide non-empty turn content.",
            "summary_label_required" => "Provide a non-empty summary label.",
            "summary_markdown_required" => "Provide non-empty markdown content for the summary.",
            _ => $"Task session operation failed: {code}"
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
