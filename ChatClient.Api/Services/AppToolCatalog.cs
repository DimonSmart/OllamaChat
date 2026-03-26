using System.Text.Json;
using ChatClient.Domain.Models;
using ModelContextProtocol.Client;

namespace ChatClient.Api.Services;

public enum AppToolPlannerRole
{
    Discover,
    Acquire,
    Transform,
    Act
}

public enum AppToolProducesKind
{
    Reference,
    Document,
    StructuredData,
    SideEffect
}

public sealed record AppToolDescriptor(
    string QualifiedName,
    string ServerName,
    string ToolName,
    string DisplayName,
    string Description,
    JsonElement InputSchema,
    JsonElement? OutputSchema,
    bool MayRequireUserInput,
    bool ReadOnlyHint,
    bool DestructiveHint,
    bool IdempotentHint,
    bool OpenWorldHint,
    Func<Dictionary<string, object?>, CancellationToken, Task<object>> ExecuteAsync,
    string? BaseQualifiedName = null,
    string? BaseServerName = null,
    Guid? BindingId = null,
    string? BindingDisplayName = null,
    AppToolPlanningMetadata? PlanningMetadata = null);

public sealed record AppToolPlanningMetadata(
    string? Purpose = null,
    string? UseWhen = null,
    string? AvoidWhen = null,
    string? Returns = null,
    string? Constraints = null,
    AppToolPlannerRole? PlannerRole = null,
    AppToolProducesKind? ProducesKind = null);

public interface IAppToolCatalog
{
    Task<IReadOnlyList<AppToolDescriptor>> ListToolsAsync(
        McpClientRequestContext? requestContext = null,
        CancellationToken cancellationToken = default);
}

public sealed class AppToolCatalog(IMcpClientService mcpClientService) : IAppToolCatalog
{
    private static readonly JsonElement EmptyObjectSchema = CreateEmptyObjectSchema();

    public async Task<IReadOnlyList<AppToolDescriptor>> ListToolsAsync(
        McpClientRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<AppToolDescriptor>();
        var clients = await mcpClientService.GetMcpClientsAsync(requestContext, cancellationToken);

        foreach (var clientHandle in clients)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var serverName = clientHandle.DisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(serverName))
                continue;

            var baseServerName = clientHandle.BaseServerName?.Trim();
            if (string.IsNullOrWhiteSpace(baseServerName))
                continue;

            var bindingId = clientHandle.BindingId;
            var bindingDisplayName = clientHandle.BindingDisplayName?.Trim();

            var tools = await mcpClientService.GetMcpTools(clientHandle.Client, cancellationToken);
            foreach (var tool in tools)
            {
                var toolName = tool.Name?.Trim();
                if (string.IsNullOrWhiteSpace(toolName))
                    continue;

                var description = McpBindingPresentation.BuildToolDescription(tool.Description, clientHandle.Binding);
                var inputSchema = tool.JsonSchema.ValueKind == JsonValueKind.Undefined
                    ? EmptyObjectSchema.Clone()
                    : tool.JsonSchema.Clone();
                JsonElement? outputSchema = tool.ReturnJsonSchema is { } returnJsonSchema &&
                                            returnJsonSchema.ValueKind != JsonValueKind.Undefined
                    ? returnJsonSchema.Clone()
                    : null;
                var annotations = tool.ProtocolTool.Annotations;
                var readOnlyHint = annotations?.ReadOnlyHint ?? false;
                var destructiveHint = annotations?.DestructiveHint ?? false;
                var idempotentHint = annotations?.IdempotentHint ?? false;
                var openWorldHint = annotations?.OpenWorldHint ?? false;

                result.Add(new AppToolDescriptor(
                    QualifiedName: bindingId is Guid value && value != Guid.Empty
                        ? $"binding:{value:N}:{toolName}"
                        : $"{baseServerName}:{toolName}",
                    ServerName: serverName,
                    ToolName: toolName,
                    DisplayName: string.IsNullOrWhiteSpace(tool.Title) ? toolName : tool.Title,
                    Description: description,
                    InputSchema: inputSchema,
                    OutputSchema: outputSchema,
                    MayRequireUserInput: MayRequireUserInput(description),
                    ReadOnlyHint: readOnlyHint,
                    DestructiveHint: destructiveHint,
                    IdempotentHint: idempotentHint,
                    OpenWorldHint: openWorldHint,
                    ExecuteAsync: async (arguments, token) => await tool.CallAsync(arguments, null, null, token),
                    BaseQualifiedName: $"{baseServerName}:{toolName}",
                    BaseServerName: baseServerName,
                    BindingId: bindingId,
                    BindingDisplayName: bindingDisplayName,
                    PlanningMetadata: InferPlanningMetadata(outputSchema, readOnlyHint, destructiveHint, openWorldHint, description)));
            }
        }

        return result
            .OrderBy(static tool => tool.ServerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static tool => tool.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AppToolPlanningMetadata? InferPlanningMetadata(
        JsonElement? outputSchema,
        bool readOnlyHint,
        bool destructiveHint,
        bool openWorldHint,
        string description)
    {
        var producesKind = InferProducesKind(outputSchema, readOnlyHint, destructiveHint);
        var plannerRole = InferPlannerRole(producesKind, readOnlyHint, destructiveHint, openWorldHint);
        if (plannerRole is null && producesKind is null)
            return null;

        return new AppToolPlanningMetadata(
            Constraints: BuildConstraintHint(description, producesKind),
            PlannerRole: plannerRole,
            ProducesKind: producesKind);
    }

    private static AppToolPlannerRole? InferPlannerRole(
        AppToolProducesKind? producesKind,
        bool readOnlyHint,
        bool destructiveHint,
        bool openWorldHint)
    {
        if (destructiveHint || !readOnlyHint)
            return AppToolPlannerRole.Act;

        return producesKind switch
        {
            AppToolProducesKind.Reference when openWorldHint => AppToolPlannerRole.Discover,
            AppToolProducesKind.Document => AppToolPlannerRole.Acquire,
            AppToolProducesKind.StructuredData => AppToolPlannerRole.Transform,
            AppToolProducesKind.SideEffect => AppToolPlannerRole.Act,
            _ => null
        };
    }

    private static AppToolProducesKind? InferProducesKind(
        JsonElement? outputSchema,
        bool readOnlyHint,
        bool destructiveHint)
    {
        if (destructiveHint || !readOnlyHint)
            return AppToolProducesKind.SideEffect;
        if (outputSchema is not JsonElement schema)
            return null;
        if (HasArrayProperty(schema, "results"))
            return AppToolProducesKind.Reference;
        if (HasStringProperty(schema, "content") || HasStringProperty(schema, "text") || HasStringProperty(schema, "markdown"))
            return AppToolProducesKind.Document;

        return AppToolProducesKind.StructuredData;
    }

    private static string? BuildConstraintHint(string description, AppToolProducesKind? producesKind)
    {
        if (producesKind == AppToolProducesKind.Reference)
            return "Produces candidate references, not verified entities.";
        if (producesKind == AppToolProducesKind.Document)
            return "Produces raw content or documents, not verified conclusions.";

        if (string.IsNullOrWhiteSpace(description))
            return null;
        if (description.Contains("candidate", StringComparison.OrdinalIgnoreCase))
            return "Returned items are candidates and may require verification.";

        return null;
    }

    private static bool HasArrayProperty(JsonElement schema, string propertyName)
    {
        if (!TryGetSchemaProperty(schema, propertyName, out var propertySchema))
            return false;

        return SchemaAllowsType(propertySchema, "array");
    }

    private static bool HasStringProperty(JsonElement schema, string propertyName)
    {
        if (!TryGetSchemaProperty(schema, propertyName, out var propertySchema))
            return false;

        return SchemaAllowsType(propertySchema, "string");
    }

    private static bool TryGetSchemaProperty(JsonElement schema, string propertyName, out JsonElement propertySchema)
    {
        propertySchema = default;
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty(propertyName, out propertySchema))
        {
            return false;
        }

        propertySchema = propertySchema.Clone();
        return true;
    }

    private static bool SchemaAllowsType(JsonElement schema, string expectedType)
    {
        if (!schema.TryGetProperty("type", out var typeElement))
            return false;

        if (typeElement.ValueKind == JsonValueKind.String)
            return string.Equals(typeElement.GetString(), expectedType, StringComparison.OrdinalIgnoreCase);

        return typeElement.ValueKind == JsonValueKind.Array
            && typeElement.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.String
                && string.Equals(item.GetString(), expectedType, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MayRequireUserInput(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return false;

        return description.Contains("elicitation", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("ask user", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("asks user", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("asks the user", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("prompt", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement CreateEmptyObjectSchema()
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
        return document.RootElement.Clone();
    }
}
