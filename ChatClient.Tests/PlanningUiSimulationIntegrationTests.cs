using ChatClient.Api;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Host;
using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Api.Services.Seed;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Xunit.Abstractions;

namespace ChatClient.Tests;

public sealed class PlanningUiSimulationIntegrationTests(ITestOutputHelper output)
{
    private const string OpenAiServerName = "OpenAI";
    private const string OpenAiModelName = "gpt-5.4-mini";
    private const int PollDelayMilliseconds = 500;
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(7);

    [RealWebFact]
    [Trait("Category", "RealWebExploration")]
    [Trait("Category", "UiSimulation")]
    public async Task ChatPlanningUi_WithBuiltInWebBinding_RunsEndToEnd_ForRobotVacuumQuery_UsingOpenAiMini()
    {
        const string userQuery = "Find two popular robot vacuum cleaners, compare their specs, and recommend which one is better.";

        var storageRoot = Path.Combine(
            Path.GetTempPath(),
            "OllamaChat-ui-simulation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storageRoot);

        await using var serviceProvider = BuildServiceProvider(storageRoot);
        await SeedApplicationDataAsync(serviceProvider);

        await using var scope = serviceProvider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var llmServerConfigService = services.GetRequiredService<ILlmServerConfigService>();
        var appToolCatalog = services.GetRequiredService<IAppToolCatalog>();
        var planningSessionService = services.GetRequiredService<IPlanningSessionService>();

        var openAiServer = (await llmServerConfigService.GetAllAsync())
            .FirstOrDefault(server =>
                server.Id.HasValue &&
                string.Equals(server.Name, OpenAiServerName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"LLM server '{OpenAiServerName}' was not found.");

        var plannerDraft = AgentTemplateBuilder
            .New("Planner", "planner")
            .WithBinding(
                BuiltInWebMcpServerTools.Descriptor.Id ?? throw new InvalidOperationException("Built-in web server ID is missing."),
                BuiltInWebMcpServerTools.Descriptor.Name,
                static binding => binding
                    .Enabled()
                    .OnlyTools("search", "download"))
            .Build();

        var requestContext = new McpClientRequestContext(plannerDraft.McpServerBindings);
        var availableTools = McpBindingToolSelectionResolver.FilterAvailableTools(
            plannerDraft.McpServerBindings,
            await appToolCatalog.ListToolsAsync(requestContext));

        output.WriteLine($"Storage root: {storageRoot}");
        output.WriteLine($"Using model: {openAiServer.Name} / {OpenAiModelName}");
        output.WriteLine($"Discovered tools: {string.Join(", ", availableTools.Select(static tool => tool.QualifiedName))}");

        Assert.Contains(availableTools, static tool => string.Equals(tool.ToolName, "search", StringComparison.Ordinal));
        Assert.Contains(availableTools, static tool => string.Equals(tool.ToolName, "download", StringComparison.Ordinal));

        var resolvedPlanner = ResolvedChatAgentFactory.Resolve(
            plannerDraft,
            new ServerModel(openAiServer.Id!.Value, OpenAiModelName));

        try
        {
            await planningSessionService.StartAsync(new PlanningRunRequest
            {
                Planner = resolvedPlanner,
                UserQuery = userQuery
            });

            await WaitForCompletionAsync(planningSessionService, RunTimeout);
        }
        finally
        {
            await planningSessionService.CancelAsync();
            DumpState(planningSessionService.State);
        }

        Assert.NotNull(planningSessionService.State.FinalResult);
        Assert.True(
            planningSessionService.State.FinalResult!.Ok,
            $"Planning run must finish successfully. Error code: {planningSessionService.State.FinalResult.Error?.Code ?? "<none>"}");
        AssertVacuumLowLevelGraphUsesSearchThenDownload(planningSessionService.State);
    }

    private async Task WaitForCompletionAsync(
        IPlanningSessionService planningSessionService,
        TimeSpan timeout)
    {
        var startedAt = DateTime.UtcNow;
        while (planningSessionService.State.IsRunning || !planningSessionService.State.IsCompleted)
        {
            if (DateTime.UtcNow - startedAt > timeout)
            {
                throw new TimeoutException($"Planning run did not complete within {timeout}.");
            }

            await Task.Delay(PollDelayMilliseconds);
        }
    }

    private void DumpState(PlanningSessionState state)
    {
        output.WriteLine(string.Empty);
        output.WriteLine("=== FINAL STATE ===");
        output.WriteLine($"IsRunning: {state.IsRunning}");
        output.WriteLine($"IsCompleted: {state.IsCompleted}");
        output.WriteLine($"HasRequestBrief: {state.RequestBrief is not null}");
        output.WriteLine($"HasOutlinePlan: {state.OutlinePlan is not null}");
        output.WriteLine($"HasLowLevelPlan: {state.LowLevelPlan is not null}");
        output.WriteLine($"HasRuntimePlan: {state.RuntimePlan is not null}");
        output.WriteLine($"HasFinalResult: {state.FinalResult is not null}");

        if (state.FinalResult is not null)
        {
            output.WriteLine("FinalResult:");
            output.WriteLine(SerializeJson(state.FinalResult));
        }

        lock (state.Issues)
        {
            if (state.Issues.Count > 0)
            {
                output.WriteLine("Issues:");
                output.WriteLine(SerializeJson(state.Issues));
            }
        }

        lock (state.LogLines)
        {
            if (state.LogLines.Count > 0)
            {
                output.WriteLine("Log:");
                output.WriteLine(string.Join(Environment.NewLine, state.LogLines));
            }
        }
    }

    private static async Task SeedApplicationDataAsync(ServiceProvider serviceProvider)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var services = scope.ServiceProvider;

        await services.GetRequiredService<LlmServerConfigSeeder>().SeedAsync();
        await services.GetRequiredService<McpServerConfigSeeder>().SeedAsync();
    }

    private static ServiceProvider BuildServiceProvider(string storageRoot)
    {
        var apiRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ChatClient.Api"));
        var environment = new StubHostEnvironment(apiRoot);
        var configuration = new ConfigurationBuilder()
            .SetBasePath(apiRoot)
            .AddJsonFile("appsettings.json", optional: false)
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:RootPath"] = storageRoot
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddLogging(static builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.AddApplicationServices(configuration, environment);

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static string SerializeJson<T>(T value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = true
        });

    private static void AssertVacuumLowLevelGraphUsesSearchThenDownload(PlanningSessionState state)
    {
        Assert.NotNull(state.OutlinePlan);
        Assert.NotNull(state.LowLevelPlan);

        var outlineKindsByNodeId = state.OutlinePlan!.Nodes.ToDictionary(node => node.Id, node => node.Kind, StringComparer.OrdinalIgnoreCase);
        var discoverSteps = state.LowLevelPlan!.Steps
            .Where(step => outlineKindsByNodeId.TryGetValue(step.OutlineNodeId, out var kind)
                && string.Equals(kind, "discover", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var acquireSteps = state.LowLevelPlan.Steps
            .Where(step => outlineKindsByNodeId.TryGetValue(step.OutlineNodeId, out var kind)
                && string.Equals(kind, "acquire", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Contains(discoverSteps, static step => string.Equals(step.CapabilityId?.Split(':').Last(), "search", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(acquireSteps, static step => string.Equals(step.CapabilityId?.Split(':').Last(), "download", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(acquireSteps, static step => string.Equals(step.CapabilityId?.Split(':').Last(), "search", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StubHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "ChatClient.Tests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
