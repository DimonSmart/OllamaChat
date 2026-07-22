using ChatClient.Api;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services;
using ChatClient.Application.Services.Agentic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Moq;
using System.Text.Json;

namespace ChatClient.Tests;

public class AgenticToolSetBuilderTests
{
    [Fact]
    public void AddApplicationServices_BindsToolPolicyConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChatEngine:ToolPolicy:TimeoutSeconds"] = "11",
                ["ChatEngine:ToolPolicy:InteractiveTimeoutSeconds"] = "17",
                ["ChatEngine:ToolPolicy:MaxRetries"] = "3",
                ["ChatEngine:ToolPolicy:RetryDelayMs"] = "29"
            })
            .Build();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(static value => value.EnvironmentName).Returns(Environments.Production);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddApplicationServices(configuration, environment.Object);
        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<IOptions<AgenticToolInvocationPolicyOptions>>().Value;

        Assert.Equal(11, policy.TimeoutSeconds);
        Assert.Equal(17, policy.InteractiveTimeoutSeconds);
        Assert.Equal(3, policy.MaxRetries);
        Assert.Equal(29, policy.RetryDelayMs);
    }

    [Fact]
    public void NormalizeToolPolicy_InteractiveTimeoutIsNeverShorterThanRegularTimeout()
    {
        var normalized = AgenticRuntimeAgentFactory.NormalizeToolPolicy(new AgenticToolInvocationPolicyOptions
        {
            TimeoutSeconds = 20,
            InteractiveTimeoutSeconds = 5,
            MaxRetries = 2,
            RetryDelayMs = 7
        });

        Assert.Equal(20, normalized.TimeoutSeconds);
        Assert.Equal(20, normalized.InteractiveTimeoutSeconds);
        Assert.Equal(2, normalized.MaxRetries);
        Assert.Equal(7, normalized.RetryDelayMs);
    }

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

    [Fact]
    public async Task InvokeAsync_RetriesAndKeepsChatInteractionScopePerAttempt()
    {
        var attempts = 0;
        var interaction = new TrackingInteractionService();
        var toolSet = AgenticToolSetBuilder.Build(
            ["docs:search"],
            [CreateTool("docs", "search", executeAsync: (_, _) =>
            {
                attempts++;
                return attempts == 1
                    ? Task.FromException<object>(new InvalidOperationException("transient"))
                    : Task.FromResult<object>("recovered");
            })],
            new AgenticToolInvocationPolicyOptions
            {
                MaxRetries = 1,
                RetryDelayMs = 10
            },
            interaction,
            NullLogger.Instance);

        var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(toolSet.MetadataByName).Value.Tool);
        var result = await function.InvokeAsync(new AIFunctionArguments());

        Assert.Equal("recovered", result?.ToString());
        Assert.Equal(2, attempts);
        Assert.Equal(2, interaction.ScopeStarts);
        Assert.Equal(2, interaction.ScopeDisposals);
        Assert.Equal(0, interaction.ActiveScopes);
    }

    [Fact]
    public async Task InvokeAsync_CallerCancellationDoesNotRetryAndReleasesScope()
    {
        var attempts = 0;
        var interaction = new TrackingInteractionService();
        var toolSet = AgenticToolSetBuilder.Build(
            ["docs:search"],
            [CreateTool("docs", "search", executeAsync: async (_, cancellationToken) =>
            {
                attempts++;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "never";
            })],
            new AgenticToolInvocationPolicyOptions { MaxRetries = 3 },
            interaction,
            NullLogger.Instance);
        var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(toolSet.MetadataByName).Value.Tool);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await function.InvokeAsync(new AIFunctionArguments(), cancellation.Token));

        Assert.Equal(0, attempts);
        Assert.Equal(0, interaction.ActiveScopes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_RegularAndInteractiveTimeoutsRetryAndReleaseScope(bool interactive)
    {
        var attempts = 0;
        var interaction = new TrackingInteractionService();
        var toolSet = AgenticToolSetBuilder.Build(
            ["docs:search"],
            [CreateTool(
                "docs",
                "search",
                mayRequireUserInput: interactive,
                executeAsync: async (_, cancellationToken) =>
                {
                    attempts++;
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return "never";
                })],
            new AgenticToolInvocationPolicyOptions
            {
                TimeoutSeconds = interactive ? 10 : 1,
                InteractiveTimeoutSeconds = interactive ? 1 : 10,
                MaxRetries = 1
            },
            interaction,
            NullLogger.Instance);
        var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(toolSet.MetadataByName).Value.Tool);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await function.InvokeAsync(new AIFunctionArguments()));

        Assert.Equal(2, attempts);
        Assert.Equal(2, interaction.ScopeStarts);
        Assert.Equal(2, interaction.ScopeDisposals);
        Assert.Equal(0, interaction.ActiveScopes);
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
        JsonElement? outputSchema = null,
        bool mayRequireUserInput = false,
        Func<Dictionary<string, object?>, CancellationToken, Task<object>>? executeAsync = null)
    {
        return new AppToolDescriptor(
            QualifiedName: $"{serverName}:{toolName}",
            ServerName: serverName,
            ToolName: toolName,
            DisplayName: toolName,
            Description: $"{toolName} tool from {serverName}",
            InputSchema: inputSchema ?? CreateSchema(),
            OutputSchema: outputSchema,
            MayRequireUserInput: mayRequireUserInput,
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            OpenWorldHint: false,
            ExecuteAsync: executeAsync ?? ((_, _) => Task.FromResult<object>("ok")),
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

    private sealed class TrackingInteractionService : IMcpUserInteractionService
    {
        public int ScopeStarts { get; private set; }
        public int ScopeDisposals { get; private set; }
        public int ActiveScopes { get; private set; }

        public IDisposable BeginInteractionScope(McpInteractionScope scope)
        {
            Assert.Equal(McpInteractionScope.Chat, scope);
            ScopeStarts++;
            ActiveScopes++;
            return new CallbackDisposable(() =>
            {
                ScopeDisposals++;
                ActiveScopes--;
            });
        }

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

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            callback();
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
