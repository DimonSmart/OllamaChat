using ChatClient.Api.Client.Services;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Client.Services.Formatters;
using ChatClient.Api.PlanningRuntime.Host;
using ChatClient.Api.Services;
using ChatClient.Api.Services.Rag;
using ChatClient.Api.Services.Seed;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Infrastructure.Repositories;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MudBlazor.Services;
using System.Net.Http;

namespace ChatClient.Api;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        services.AddCoreServices();
        services.AddPersistenceServices();
        services.AddRagServices();
        services.AddAgenticServices();
        services.AddSeeders();
        services.AddUiServices(environment);
        services.AddHttpClients();

        return services;
    }

    private static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IMcpServerConfigService, McpServerConfigService>();
        services.AddSingleton<ILlmServerConfigService, LlmServerConfigService>();
        services.AddSingleton<IMcpClientService, McpClientService>();
        services.AddSingleton<IAppToolCatalog, AppToolCatalog>();
        services.AddSingleton<McpSamplingService>();
        services.AddSingleton<IMcpUserInteractionService, McpUserInteractionService>();
        services.AddSingleton<KernelService>();
        services.AddSingleton<IOllamaClientService, OllamaService>();
        services.AddSingleton<IOpenAIClientService, OpenAIClientService>();
        services.AddSingleton<IModelCapabilityService, ModelCapabilityService>();
        services.AddSingleton<IServerConnectionTestService, ServerConnectionTestService>();
        services.AddSingleton<McpFunctionIndexService>();
        services.AddScoped<OllamaServerAvailabilityService>();

        return services;
    }

    private static IServiceCollection AddPersistenceServices(this IServiceCollection services)
    {
        services.AddSingleton<IAgentDescriptionRepository, AgentDescriptionRepository>();
        services.AddSingleton<ILlmServerConfigRepository, LlmServerConfigRepository>();
        services.AddSingleton<IMcpServerConfigRepository, McpServerConfigRepository>();
        services.AddSingleton<IUserSettingsRepository, UserSettingsRepository>();
        services.AddSingleton<IAgentDescriptionService, AgentDescriptionService>();
        services.AddSingleton<ISavedChatRepository, SavedChatRepository>();
        services.AddSingleton<IUserSettingsService, UserSettingsService>();
        services.AddSingleton<ISavedChatService, SavedChatService>();
        services.AddSingleton<IFileConverter, MarkdownFileConverter>();
        services.AddSingleton<IFileConverter, FileConverter>();

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
        services.AddScoped<IAgenticExecutionRuntime, HttpAgenticExecutionRuntime>();
        services.AddScoped<AgenticChatEngineOrchestrator>();
        services.AddScoped<IAgenticRagContextService, AgenticRagContextService>();
        services.AddScoped<AgenticChatEngineHistoryBuilder>();
        services.AddScoped<AgenticChatEngineStreamingBridge>();
        services.AddScoped<IChatEngineOrchestrator>(sp => sp.GetRequiredService<AgenticChatEngineOrchestrator>());
        services.AddScoped<IChatEngineHistoryBuilder>(sp => sp.GetRequiredService<AgenticChatEngineHistoryBuilder>());
        services.AddScoped<IChatEngineStreamingBridge>(sp => sp.GetRequiredService<AgenticChatEngineStreamingBridge>());
        services.AddScoped<AgenticChatEngineSessionService>();
        services.AddScoped<IChatEngineSessionService>(sp => sp.GetRequiredService<AgenticChatEngineSessionService>());
        services.AddScoped<IAgenticChatViewModelService, AgenticChatViewModelService>();
        services.AddScoped<IPlanningChatClientFactory, PlanningChatClientFactory>();
        services.AddScoped<IPlanningSessionService, PlanningSessionService>();
        services.AddSingleton<IChatFormatter, TextChatFormatter>();
        services.AddSingleton<IChatFormatter, MarkdownChatFormatter>();
        services.AddSingleton<IChatFormatter, HtmlChatFormatter>();

        return services;
    }

    private static IServiceCollection AddSeeders(this IServiceCollection services)
    {
        services.AddSingleton<AgentDescriptionSeeder>();
        services.AddSingleton<LlmServerConfigSeeder>();
        services.AddSingleton<McpServerConfigSeeder>();
        services.AddSingleton<RagFilesSeeder>();

        return services;
    }

    private static IServiceCollection AddUiServices(this IServiceCollection services, IHostEnvironment environment)
    {
        services.AddRazorPages();
        services.AddServerSideBlazor()
            .AddCircuitOptions(options => options.DetailedErrors = environment.IsDevelopment());

        services.AddMudServices();

        services.AddSingleton<CircuitHandler, AutoShutdownCircuitHandler>();

        services.AddControllers(options =>
        {
            options.Filters.Add<ApiExceptionFilter>();
        });

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

