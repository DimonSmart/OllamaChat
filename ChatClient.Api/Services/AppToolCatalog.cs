using System.Text.Json;
using ChatClient.Domain.Models;
using ModelContextProtocol.Client;

namespace ChatClient.Api.Services;

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
    string? BindingDisplayName = null);

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
            {
                continue;
            }

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
                    ReadOnlyHint: annotations?.ReadOnlyHint ?? false,
                    DestructiveHint: annotations?.DestructiveHint ?? false,
                    IdempotentHint: annotations?.IdempotentHint ?? false,
                    OpenWorldHint: annotations?.OpenWorldHint ?? false,
                    ExecuteAsync: async (arguments, token) => await tool.CallAsync(arguments, null, null, token),
                    BaseQualifiedName: $"{baseServerName}:{toolName}",
                    BaseServerName: baseServerName,
                    BindingId: bindingId,
                    BindingDisplayName: bindingDisplayName));
            }
        }

        return result
            .OrderBy(static tool => tool.ServerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static tool => tool.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
