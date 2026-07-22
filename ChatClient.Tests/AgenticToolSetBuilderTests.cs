using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services;
using ChatClient.Application.Services.Agentic;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using System.Text.Json;

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
            tools,
            TestPolicy,
            TestInteractionService.Instance,
            NullLogger.Instance);

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
            tools,
            TestPolicy,
            TestInteractionService.Instance,
            NullLogger.Instance);

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
            tools,
            TestPolicy,
            TestInteractionService.Instance,
            NullLogger.Instance);

        Assert.Equal(2, toolSet.MetadataByName.Count);
        Assert.Contains("Docs_A__search", toolSet.MetadataByName.Keys);
        Assert.Contains("Docs_B__search", toolSet.MetadataByName.Keys);
    }

    [Fact]
    public void Build_BooleanInputSchema_NormalizesToEmptyObject()
    {
        var toolSet = AgenticToolSetBuilder.Build(
            ["search"],
            [CreateTool("docs", "search", inputSchema: JsonSerializer.SerializeToElement(true))],
            TestPolicy,
            TestInteractionService.Instance,
            NullLogger.Instance);

        var registered = Assert.Single(toolSet.MetadataByName);
        var function = Assert.IsAssignableFrom<PolicyAgenticFunction>(registered.Value.Tool);
        var schema = function.JsonSchema;

        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.Equal("object", schema.GetProperty("type").GetString());
    }

    [Fact]
    public void Build_BooleanOutputSchema_DropsReturnSchema()
    {
        var toolSet = AgenticToolSetBuilder.Build(
            ["search"],
            [CreateTool("docs", "search", outputSchema: JsonSerializer.SerializeToElement(true))],
            TestPolicy,
            TestInteractionService.Instance,
            NullLogger.Instance);

        var registered = Assert.Single(toolSet.MetadataByName);
        var function = Assert.IsAssignableFrom<PolicyAgenticFunction>(registered.Value.Tool);

        Assert.Null(function.ReturnJsonSchema);
    }

    private static AgenticToolInvocationPolicyOptions TestPolicy { get; } = new()
    {
        TimeoutSeconds = 0,
        InteractiveTimeoutSeconds = 0,
        MaxRetries = 0,
        RetryDelayMs = 0
    };

    private static AppToolDescriptor CreateTool(
        string serverName,
        string toolName,
        string? bindingDisplayName = null,
        JsonElement? inputSchema = null,
        JsonElement? outputSchema = null)
    {
        return new AppToolDescriptor(
            QualifiedName: $"{serverName}:{toolName}",
            ServerName: serverName,
            ToolName: toolName,
            DisplayName: toolName,
            Description: $"{toolName} tool from {serverName}",
            InputSchema: inputSchema ?? CreateSchema(),
            OutputSchema: outputSchema,
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

    private sealed class TestInteractionService : IMcpUserInteractionService
    {
        public static TestInteractionService Instance { get; } = new();

        public IDisposable BeginInteractionScope(McpInteractionScope scope) => NoopDisposable.Instance;

        public IDisposable RegisterElicitationHandler(
            McpInteractionScope scope,
            Func<McpElicitationPrompt, CancellationToken, Task<McpElicitationResponse>> handler) =>
            NoopDisposable.Instance;

        public ValueTask<ElicitResult> HandleElicitationAsync(
            string serverName,
            ElicitRequestParams request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
