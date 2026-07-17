using ChatClient.Api;
using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.Client.Services.Agentic;
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
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace ChatClient.Tests;

public sealed class RealWorkflowExecutionTests(ITestOutputHelper output)
{
    private const string WorkflowNameEnvironmentVariable = "CHATCLIENT_REAL_WORKFLOW_NAME";
    private const string WorkflowParamsEnvironmentVariable = "CHATCLIENT_REAL_WORKFLOW_PARAMS_JSON";
    private const string WorkflowInitialMessageEnvironmentVariable = "CHATCLIENT_REAL_WORKFLOW_INITIAL_MESSAGE";
    private const string WorkflowServerIdEnvironmentVariable = "CHATCLIENT_REAL_WORKFLOW_SERVER_ID";
    private const string WorkflowModelEnvironmentVariable = "CHATCLIENT_REAL_WORKFLOW_MODEL";

    [RealWorkflowFact]
    public async Task RunWorkflowFromSavedDefinitions_AndPrintTranscriptAndSummary()
    {
        var options = RealWorkflowExecutionOptions.FromEnvironment();
        await using var serviceProvider = BuildServiceProvider();
        await SeedApplicationDataAsync(serviceProvider);

        await using var scope = serviceProvider.CreateAsyncScope();
        var scopedServices = scope.ServiceProvider;

        var workflowDefinitionService = scopedServices.GetRequiredService<IWorkflowDefinitionService>();
        var workflowCompiler = scopedServices.GetRequiredService<IWorkflowDefinitionCompiler>();
        var workflowMaterializer = scopedServices.GetRequiredService<IWorkflowAgentDraftMaterializer>();
        var workflowSessionService = scopedServices.GetRequiredService<IOrchestrationWorkflowSessionService>();
        var taskSessionStore = scopedServices.GetRequiredService<TaskSessionStore>();
        var userSettingsService = scopedServices.GetRequiredService<IUserSettingsService>();

        var savedWorkflow = (await workflowDefinitionService.GetAllAsync())
            .FirstOrDefault(workflow =>
                string.Equals(workflow.DisplayName, options.WorkflowName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(workflow.WorkflowId, options.WorkflowName, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(savedWorkflow);

        var compiled = await workflowCompiler.CompileAsync(savedWorkflow!.SourceCode);
        Assert.NotNull(compiled.Workflow);

        var workflow = await workflowMaterializer.MaterializeAsync(compiled.Workflow!);
        var model = await ResolveModelAsync(userSettingsService, options);
        var runtimeAgents = workflow.Participants
            .Select(agent => ResolvedChatAgentFactory.Resolve(GetRequiredAgentDraft(agent), model))
            .ToList();

        var request = new OrchestrationWorkflowSessionStartRequest
        {
            Workflow = workflow,
            Agents = runtimeAgents,
            Configuration = new AppChatConfiguration(model.ModelName, []),
            SessionTitle = $"{workflow.DisplayName} Test Run",
            SessionDescription = "Manual real workflow execution from xUnit.",
            StartInputs = BuildStartInputs(options, workflow)
        };

        await workflowSessionService.StartAsync(request);

        if (workflow.Execution.Mode == AgentWorkflowExecutionMode.Autonomous)
        {
            await workflowSessionService.KickoffAsync();
        }
        else
        {
            await workflowSessionService.SendAsync(options.InitialMessage);
        }

        var transcript = workflowSessionService.Messages.ToList();
        var taskSession = await taskSessionStore.GetSessionAsync(workflowSessionService.TaskSessionId, CancellationToken.None);

        output.WriteLine($"Workflow: {workflow.DisplayName} ({workflow.Id})");
        output.WriteLine($"SessionId: {workflowSessionService.TaskSessionId}");
        output.WriteLine($"Phase: {taskSession.Phase ?? "<null>"}");
        output.WriteLine($"TurnCount: {taskSession.TurnCount}");
        output.WriteLine(string.Empty);
        output.WriteLine("Transcript:");
        output.WriteLine(FormatTranscript(transcript));

        if (!string.IsNullOrWhiteSpace(workflow.Execution.CompletionSummaryLabel) &&
            taskSession.Summaries.Any(summary =>
                string.Equals(summary.Label, workflow.Execution.CompletionSummaryLabel, StringComparison.OrdinalIgnoreCase)))
        {
            var summary = await taskSessionStore.GetSummaryAsync(
                workflowSessionService.TaskSessionId,
                workflow.Execution.CompletionSummaryLabel!,
                CancellationToken.None);

            output.WriteLine(string.Empty);
            output.WriteLine($"Summary ({summary.Label}):");
            output.WriteLine(summary.Markdown);
        }

        Assert.NotEmpty(transcript);

        if (workflow.Execution.Mode == AgentWorkflowExecutionMode.Autonomous)
        {
            var completedByPhase =
                !string.IsNullOrWhiteSpace(workflow.Execution.CompletionPhase) &&
                string.Equals(taskSession.Phase, workflow.Execution.CompletionPhase, StringComparison.OrdinalIgnoreCase);
            var completedBySummary =
                !string.IsNullOrWhiteSpace(workflow.Execution.CompletionSummaryLabel) &&
                taskSession.Summaries.Any(summary =>
                    string.Equals(summary.Label, workflow.Execution.CompletionSummaryLabel, StringComparison.OrdinalIgnoreCase));

            Assert.True(
                completedByPhase || completedBySummary,
                $"Autonomous workflow '{workflow.DisplayName}' finished without reaching phase '{workflow.Execution.CompletionPhase}' and without saving summary '{workflow.Execution.CompletionSummaryLabel}'.");
        }
    }

    private static IReadOnlyList<OrchestrationWorkflowStartInputValue> BuildStartInputs(
        RealWorkflowExecutionOptions options,
        IOrchestrationWorkflowDefinition workflow)
    {
        if (string.IsNullOrWhiteSpace(options.ParametersJson))
        {
            return [];
        }

        using var document = JsonDocument.Parse(options.ParametersJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"{WorkflowParamsEnvironmentVariable} must contain a JSON object.");
        }

        var properties = document.RootElement.EnumerateObject()
            .ToDictionary(static property => property.Name, static property => property.Value, StringComparer.OrdinalIgnoreCase);
        List<OrchestrationWorkflowStartInputValue> inputs = [];

        foreach (var definition in workflow.StartInputs)
        {
            if (!properties.TryGetValue(definition.Key, out var jsonValue))
            {
                continue;
            }

            if (definition.Kind == WorkflowStartInputKind.MarkdownDocument &&
                jsonValue.ValueKind == JsonValueKind.Object)
            {
                inputs.Add(new OrchestrationWorkflowStartInputValue
                {
                    Key = definition.Key,
                    Value = TryReadObjectProperty(jsonValue, "value"),
                    SourceFile = TryReadObjectProperty(jsonValue, "sourceFile")
                });
                continue;
            }

            inputs.Add(new OrchestrationWorkflowStartInputValue
            {
                Key = definition.Key,
                Value = ConvertJsonValueToString(jsonValue)
            });
        }

        return inputs;
    }

    private static string ConvertJsonValueToString(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            JsonValueKind.Object => value.GetRawText(),
            JsonValueKind.Array => value.GetRawText(),
            JsonValueKind.Null => string.Empty,
            _ => value.ToString()
        };

    private static string? TryReadObjectProperty(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return ConvertJsonValueToString(property.Value);
        }

        return null;
    }

    private static async Task SeedApplicationDataAsync(ServiceProvider serviceProvider)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var services = scope.ServiceProvider;

        await services.GetRequiredService<AgentTemplateSeeder>().SeedAsync();
        await services.GetRequiredService<WorkflowDefinitionSeeder>().SeedAsync();
        await services.GetRequiredService<LlmServerConfigSeeder>().SeedAsync();
        await services.GetRequiredService<McpServerConfigSeeder>().SeedAsync();
    }

    private static async Task<ServerModel> ResolveModelAsync(
        IUserSettingsService userSettingsService,
        RealWorkflowExecutionOptions options)
    {
        if (options.ServerId.HasValue && !string.IsNullOrWhiteSpace(options.ModelName))
        {
            return new ServerModel(options.ServerId.Value, options.ModelName);
        }

        var settings = await userSettingsService.GetSettingsAsync();
        if (settings.DefaultModel.ServerId is Guid serverId &&
            serverId != Guid.Empty &&
            !string.IsNullOrWhiteSpace(settings.DefaultModel.ModelName))
        {
            return new ServerModel(serverId, settings.DefaultModel.ModelName);
        }

        throw new InvalidOperationException(
            $"No workflow model was resolved. Set {WorkflowServerIdEnvironmentVariable} and {WorkflowModelEnvironmentVariable}, or configure the application's default model.");
    }

    private static AgentTemplateDefinition GetRequiredAgentDraft(WorkflowParticipantDefinition agent) =>
        agent.AgentDraft ?? throw new InvalidOperationException(
            $"Workflow agent '{agent.Id}' does not have a materialized draft.");

    private static ServiceProvider BuildServiceProvider()
    {
        var apiRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ChatClient.Api"));
        var environment = new StubHostEnvironment(apiRoot);
        var configuration = new ConfigurationBuilder()
            .SetBasePath(apiRoot)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddLogging(static builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddApplicationServices(configuration, environment);

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static string FormatTranscript(IReadOnlyList<IAppChatMessage> transcript)
    {
        if (transcript.Count == 0)
        {
            return "<empty>";
        }

        var builder = new StringBuilder();
        foreach (var message in transcript)
        {
            var speaker = message.Role == AppChatRole.User
                ? "user"
                : message.AgentName ?? message.Role.ToString();
            builder.AppendLine($"[{speaker}]");
            builder.AppendLine(message.Content);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private sealed class StubHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "ChatClient.Tests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }

    private sealed class RealWorkflowExecutionOptions
    {
        private const string DefaultWorkflowName = "Research Brief Sequential";
        private const string DefaultParametersJson = "";
        private const string DefaultInitialMessage =
            "Compare the trade-offs of moving a small SaaS team from a monolith to microservices within the next year.";

        public string WorkflowName { get; init; } = DefaultWorkflowName;

        public string ParametersJson { get; init; } = DefaultParametersJson;

        public string InitialMessage { get; init; } = DefaultInitialMessage;

        public Guid? ServerId { get; init; }

        public string? ModelName { get; init; }

        public static RealWorkflowExecutionOptions FromEnvironment()
        {
            var workflowName = Environment.GetEnvironmentVariable(WorkflowNameEnvironmentVariable);
            var parametersJson = Environment.GetEnvironmentVariable(WorkflowParamsEnvironmentVariable);
            var initialMessage = Environment.GetEnvironmentVariable(WorkflowInitialMessageEnvironmentVariable);
            var serverIdText = Environment.GetEnvironmentVariable(WorkflowServerIdEnvironmentVariable);
            var modelName = Environment.GetEnvironmentVariable(WorkflowModelEnvironmentVariable);

            Guid? serverId = null;
            if (!string.IsNullOrWhiteSpace(serverIdText) &&
                Guid.TryParse(serverIdText, out var parsedServerId))
            {
                serverId = parsedServerId;
            }

            return new RealWorkflowExecutionOptions
            {
                WorkflowName = string.IsNullOrWhiteSpace(workflowName) ? DefaultWorkflowName : workflowName.Trim(),
                ParametersJson = string.IsNullOrWhiteSpace(parametersJson) ? DefaultParametersJson : parametersJson,
                InitialMessage = string.IsNullOrWhiteSpace(initialMessage) ? DefaultInitialMessage : initialMessage.Trim(),
                ServerId = serverId,
                ModelName = string.IsNullOrWhiteSpace(modelName) ? null : modelName.Trim()
            };
        }
    }
}
