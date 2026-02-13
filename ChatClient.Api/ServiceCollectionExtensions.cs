using ChatClient.Api.Client.Services;
using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Api.Client.Services.Formatters;
using ChatClient.Api.Services;
using ChatClient.Api.Services.Rag;
using ChatClient.Api.Services.Seed;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Infrastructure.Repositories;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using System.Net.Http;

namespace ChatClient.Api;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IMcpServerConfigService, McpServerConfigService>();
        services.AddSingleton<ILlmServerConfigService, LlmServerConfigService>();
        services.AddSingleton<IMcpClientService, McpClientService>();
        services.AddSingleton<McpSamplingService>();
        services.AddSingleton<KernelService>();
        services.AddSingleton<IOllamaClientService, OllamaService>();
        services.AddSingleton<IOpenAIClientService, OpenAIClientService>();
        services.AddSingleton<IModelCapabilityService, ModelCapabilityService>();
        services.AddSingleton<IServerConnectionTestService, ServerConnectionTestService>();
        services.AddSingleton<McpFunctionIndexService>();
        services.AddScoped<OllamaServerAvailabilityService>();
        services.AddSingleton<IAgentDescriptionRepository, AgentDescriptionRepository>();
        services.AddSingleton<ILlmServerConfigRepository, LlmServerConfigRepository>();
        services.AddSingleton<IMcpServerConfigRepository, McpServerConfigRepository>();
        services.AddSingleton<IUserSettingsRepository, UserSettingsRepository>();
        services.AddSingleton<IAgentDescriptionService, AgentDescriptionService>();
        services.AddSingleton<ISavedChatRepository, SavedChatRepository>();
        services.AddSingleton<IRagFileRepository, RagFileRepository>();
        services.AddSingleton<IRagVectorIndexRepository, RagVectorIndexRepository>();
        services.AddSingleton<IUserSettingsService, UserSettingsService>();
        services.AddSingleton<ISavedChatService, SavedChatService>();
        services.AddSingleton<IRagFileService, RagFileService>();
        services.AddSingleton<IRagContentImportService, RagContentImportService>();
        services.AddSingleton<IRagVectorIndexService, RagVectorIndexService>();
        services.AddSingleton<IRagVectorStore, RagVectorStore>();
        services.AddSingleton<RagVectorIndexBackgroundService>();
        services.AddSingleton<IRagVectorIndexBackgroundService>(sp => sp.GetRequiredService<RagVectorIndexBackgroundService>());
        services.AddHostedService(sp => sp.GetRequiredService<RagVectorIndexBackgroundService>());
        services.AddSingleton<IRagVectorSearchService, RagVectorSearchService>();
        services.AddSingleton<IFileConverter, MarkdownFileConverter>();
        services.AddSingleton<IFileConverter, FileConverter>();
        services.AddScoped<IAgenticExecutionRuntime, HttpAgenticExecutionRuntime>();
        services.AddScoped<AgenticChatEngineOrchestrator>();
        services.AddScoped<IAgenticRagContextService, AgenticRagContextService>();
        services.AddScoped<AgenticChatEngineHistoryBuilder>();
        services.AddScoped<AgenticChatEngineStreamingBridge>();
        services.AddScoped<IChatEngineOrchestrator>(sp => sp.GetRequiredService<AgenticChatEngineOrchestrator>());
        services.AddScoped<IChatEngineHistoryBuilder>(sp => sp.GetRequiredService<AgenticChatEngineHistoryBuilder>());
        services.AddScoped<IChatEngineStreamingBridge>(sp => sp.GetRequiredService<AgenticChatEngineStreamingBridge>());
        services.AddScoped<AgenticChatEngineSessionService>();
        services.AddScoped<IAgenticAppChatService, AgenticAppChatService>();
        services.AddScoped<IAgenticChatViewModelService, AgenticChatViewModelService>();
        services.AddSingleton<IChatFormatter, TextChatFormatter>();
        services.AddSingleton<IChatFormatter, MarkdownChatFormatter>();
        services.AddSingleton<IChatFormatter, HtmlChatFormatter>();

        services.AddSingleton<AgentDescriptionSeeder>();
        services.AddSingleton<LlmServerConfigSeeder>();
        services.AddSingleton<McpServerConfigSeeder>();
        services.AddSingleton<RagFilesSeeder>();

        services.AddRazorPages();
        services.AddServerSideBlazor();

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

        services.AddMudServices();

        services.AddSingleton<CircuitHandler, AutoShutdownCircuitHandler>();

        services.AddControllers(options =>
        {
            options.Filters.Add<ApiExceptionFilter>();
        });

        return services;
    }
}

