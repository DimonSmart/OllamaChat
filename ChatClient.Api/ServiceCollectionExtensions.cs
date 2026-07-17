using ChatClient.Api.AgentWorkflows;
using ChatClient.Api.AgentWorkflows.GroupChat;
using ChatClient.Api.AgentWorkflows.Runtime;
using ChatClient.Api.Client.Services;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Api.Services.Rag;
using ChatClient.Api.Services.Seed;
using ChatClient.Api.VoiceInput;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Application.Services.TaskSessions;
using ChatClient.Infrastructure.Repositories;
using ChatClient.Infrastructure.Services.TaskSessions;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MudBlazor.Services;
using System.Net.Http;
using ChatClient.Api.Services.AgentRuntime;

namespace ChatClient.Api;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddCoreServices();
        services.AddPersistenceServices();
        services.AddRagServices();
        services.AddAgenticServices();
        services.AddSeeders();
        services.AddUiServices(configuration, environment);
        services.AddHttpClients();

        return services;
    }

    private static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IAgentWorkflowCatalog, AgentWorkflowCatalog>();
        services.AddSingleton<IWorkflowDefinitionCompiler, WorkflowDefinitionCompiler>();
        services.AddSingleton<GroupChatManagerRegistry>();
        services.AddSingleton<IOrchestrationRuntimeWorkflowBuilder, HandoffRuntimeWorkflowBuilder>();
        services.AddSingleton<IOrchestrationRuntimeWorkflowBuilder, GroupChatRuntimeWorkflowBuilder>();
        services.AddSingleton<IOrchestrationRuntimeWorkflowBuilder, SequentialRuntimeWorkflowBuilder>();
        services.AddSingleton<IOrchestrationRuntimeWorkflowBuilder, ConcurrentRuntimeWorkflowBuilder>();
        services.AddSingleton(new McpServerSessionContext(null));
        services.AddSingleton<IMcpServerConfigService, McpServerConfigService>();
        services.AddSingleton<ILlmServerConfigService, LlmServerConfigService>();
        services.AddSingleton<IMcpClientService, McpClientService>();
        services.AddSingleton<IAppToolCatalog, AppToolCatalog>();
        services.AddSingleton<McpSamplingService>();
        services.AddSingleton<IMcpUserInteractionService, McpUserInteractionService>();
        services.AddSingleton<UserProfilePreferencesEditorService>();
        services.AddSingleton<TaskSessionStore>();
        services.AddSingleton<MarkdownDocumentIntakeService>();
        services.AddSingleton<KernelService>();
        services.AddSingleton<IOllamaClientService, OllamaService>();
        services.AddSingleton<IOpenAIClientService, OpenAIClientService>();
        services.AddSingleton<IModelCapabilityService, ModelCapabilityService>();
        services.AddSingleton<IServerConnectionTestService, ServerConnectionTestService>();
        services.AddSingleton<McpFunctionIndexService>();
        services.AddScoped<OllamaServerAvailabilityService>();
        services.AddOptions<VoiceInputOptions>()
            .BindConfiguration(VoiceInputOptions.SectionName);
        services.AddSingleton<IVoiceInputService, VoiceInputService>();

        return services;
    }

    private static IServiceCollection AddPersistenceServices(this IServiceCollection services)
    {
        services.AddSingleton<IAgentTemplateRepository, AgentTemplateRepository>();
        services.AddSingleton<IWorkflowDefinitionRepository, WorkflowDefinitionRepository>();
        services.AddSingleton<ILlmServerConfigRepository, LlmServerConfigRepository>();
        services.AddSingleton<IMcpServerConfigRepository, McpServerConfigRepository>();
        services.AddSingleton<IUserSettingsRepository, UserSettingsRepository>();
        services.AddSingleton<ITaskSessionRepository, SqliteTaskSessionRepository>();
        services.AddSingleton<IWorkflowExecutionPolicy, WorkflowExecutionPolicy>();
        services.AddSingleton<IAgentTemplateService, AgentTemplateService>();
        services.AddSingleton<IWorkflowDefinitionService, WorkflowDefinitionService>();
        services.AddSingleton<ISavedChatRepository, SavedChatRepository>();
        services.AddSingleton<IUserSettingsService, UserSettingsService>();
        services.AddSingleton<ISavedChatService, SavedChatService>();

        return services;
    }

    private static IServiceCollection AddRagServices(this IServiceCollection services)
    {
        services.AddSingleton<IRagFileRepository, RagFileRepository>();
        services.AddSingleton<IRagFileService, RagFileService>();
        services.AddSingleton<IRagVectorIndexService, RagVectorIndexService>();
        services.AddSingleton<IRagVectorStore, RagVectorStore>();
        services.AddSingleton<RagVectorIndexBackgroundService>();
        services.AddSingleton<IRagVectorIndexBackgroundService>(sp => sp.GetRequiredService<RagVectorIndexBackgroundService>());
        services.AddHostedService(sp => sp.GetRequiredService<RagVectorIndexBackgroundService>());
        services.AddSingleton<IRagVectorSearchService, RagVectorSearchService>();

        return services;
    }

    private static IServiceCollection AddAgenticServices(this IServiceCollection services)
    {
        services.AddScoped<AgenticRuntimeAgentFactory>();
        services.AddScoped<IAgenticExecutionRuntime, HttpAgenticExecutionRuntime>();
        services.AddScoped<IAgenticExecutionInvoker, AgenticExecutionInvoker>();
        services.AddScoped<AgenticChatEngineOrchestrator>();
        services.AddScoped<IAgenticRagContextService, AgenticRagContextService>();
        services.AddScoped<AgenticChatEngineHistoryBuilder>();
        services.AddScoped<AgenticChatEngineStreamingBridge>();
        services.AddScoped<OrchestrationWorkflowSessionBootstrapper>();
        services.AddScoped<OrchestrationWorkflowTurnCoordinator>();
        services.AddScoped<OrchestrationWorkflowEventStreamProcessor>();
        services.AddScoped<OrchestrationWorkflowPassExecutor>();
        services.AddScoped<IHeadlessWorkflowRunner, HeadlessWorkflowRunner>();
        services.AddScoped<IOrchestrationWorkflowSessionService, OrchestrationWorkflowChatSessionService>();
        services.AddScoped<IOrchestrationWorkflowChatViewModelService, OrchestrationWorkflowChatViewModelService>();
        services.AddScoped<IAgentDefinitionCatalog, AgentDefinitionCatalog>();
        services.AddScoped<IAgentSessionDefinitionResolver, AgentSessionDefinitionResolver>();
        services.AddScoped<IWorkflowParticipantResolver, WorkflowParticipantResolver>();
        services.AddScoped<IWorkflowAgentDraftMaterializer, WorkflowAgentDraftMaterializer>();
        services.AddScoped<IAgentInputDefinitionProvider, AgentInputDefinitionProvider>();
        services.AddScoped<LlmAgentRuntimeFactory>();
        services.AddScoped<ILlmAgentRuntimeFactory>(sp => sp.GetRequiredService<LlmAgentRuntimeFactory>());
        services.AddScoped<IInlineLlmAgentRuntimeFactory>(sp => sp.GetRequiredService<LlmAgentRuntimeFactory>());
        services.AddScoped<IWorkflowAgentRuntimeFactory, WorkflowAgentRuntimeFactory>();
        services.AddScoped<IAgentRuntimeFactory, AgentRuntimeFactory>();
        services.AddScoped<IAgentRuntimeProtocolExecutor, AgentRuntimeProtocolExecutor>();
        services.AddScoped<IAgentRunner, AgentRunner>();
        services.AddScoped<IWorkflowParticipantExecutor, WorkflowParticipantExecutor>();
        services.AddScoped<IChatEngineOrchestrator>(sp => sp.GetRequiredService<AgenticChatEngineOrchestrator>());
        services.AddScoped<IChatEngineHistoryBuilder>(sp => sp.GetRequiredService<AgenticChatEngineHistoryBuilder>());
        services.AddScoped<IChatEngineStreamingBridge>(sp => sp.GetRequiredService<AgenticChatEngineStreamingBridge>());
        services.AddScoped<AgenticChatEngineSessionService>();
        services.AddScoped<UnifiedAgentRuntimeChatSessionService>();
        services.AddScoped<IChatEngineSessionService>(sp => sp.GetRequiredService<UnifiedAgentRuntimeChatSessionService>());
        services.AddScoped<IAgenticChatViewModelService, AgenticChatViewModelService>();
        services.AddScoped<ILlmChatClientFactory, LlmChatClientFactory>();

        return services;
    }

    private static IServiceCollection AddSeeders(this IServiceCollection services)
    {
        services.AddSingleton<AgentTemplateSeeder>();
        services.AddSingleton<WorkflowDefinitionSeeder>();
        services.AddSingleton<LlmServerConfigSeeder>();
        services.AddSingleton<McpServerConfigSeeder>();
        services.AddSingleton<RagFilesSeeder>();

        return services;
    }

    private static IServiceCollection AddUiServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var signalRTimeouts = BlazorSignalRTimeoutOptions.FromConfiguration(configuration);

        services.AddOptions<BlazorSignalRTimeoutOptions>()
            .BindConfiguration(BlazorSignalRTimeoutOptions.SectionName)
            .PostConfigure(static options =>
            {
                var normalized = options.Normalize();
                options.ServerTimeoutSeconds = normalized.ServerTimeoutSeconds;
                options.ClientTimeoutSeconds = normalized.ClientTimeoutSeconds;
                options.HandshakeTimeoutSeconds = normalized.HandshakeTimeoutSeconds;
                options.KeepAliveIntervalSeconds = normalized.KeepAliveIntervalSeconds;
            });

        services.AddRazorPages();
        services.AddServerSideBlazor()
            .AddCircuitOptions(options => options.DetailedErrors = environment.IsDevelopment())
            .AddHubOptions(options =>
            {
                options.ClientTimeoutInterval = signalRTimeouts.ClientTimeout;
                options.HandshakeTimeout = signalRTimeouts.HandshakeTimeout;
                options.KeepAliveInterval = signalRTimeouts.KeepAliveInterval;
            });

        services.AddMudServices();
        services.AddSingleton<IMcpServerInternalEditorRegistry, McpServerInternalEditorRegistry>();

        services.AddSingleton<CircuitHandler, AutoShutdownCircuitHandler>();

        return services;
    }

    private static IServiceCollection AddHttpClients(this IServiceCollection services)
    {
        services.AddTransient<HttpLoggingHandler>();
        services.AddHttpClient();
        services.AddHttpClient("ollama")
            .AddHttpMessageHandler<HttpLoggingHandler>();
        services.AddHttpClient("ollama-insecure")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            })
            .AddHttpMessageHandler<HttpLoggingHandler>();

        return services;
    }
}

