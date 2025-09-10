using ChatClient.Api;
using ChatClient.Api.Client.Services;
using ChatClient.Api.Client.Services.Formatters;
using ChatClient.Api.Services;
using ChatClient.Api.Services.Rag;
using ChatClient.Api.Repositories;
using ChatClient.Shared.Constants;
using ChatClient.Shared.Models;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Connectors.InMemory;
using MudBlazor.Services;
using Serilog;
using System.Text;

// Enable UTF-8 for proper Cyrillic support.
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("Logs/ollamachat-.log", rollingInterval: RollingInterval.Day, fileSizeLimitBytes: 10_000_000, rollOnFileSizeLimit: true, retainedFileCountLimit: 5)
    .WriteTo.Debug()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
}

// For single-file deployment compatibility only in production
if (builder.Environment.IsProduction())
{
    var exeFolder = AppContext.BaseDirectory;
    var webRootPath = Path.Combine(exeFolder, "wwwroot");
    Directory.SetCurrentDirectory(exeFolder);
    builder.Environment.WebRootPath = webRootPath;
    builder.Environment.ContentRootPath = exeFolder;
}



builder.Services.AddSingleton<ChatClient.Shared.Services.IMcpServerConfigService, ChatClient.Api.Services.McpServerConfigService>();
builder.Services.AddSingleton<ChatClient.Api.Services.ILlmServerConfigService, ChatClient.Api.Services.LlmServerConfigService>();
builder.Services.AddSingleton<ChatClient.Api.Services.IMcpClientService, ChatClient.Api.Services.McpClientService>();
builder.Services.AddSingleton<ChatClient.Api.Services.McpSamplingService>();
builder.Services.AddSingleton<ChatClient.Api.Services.KernelService>();
builder.Services.AddSingleton<IOllamaClientService, OllamaService>();
builder.Services.AddSingleton<IOpenAIClientService, OpenAIClientService>();
builder.Services.AddSingleton<IServerConnectionTestService, ServerConnectionTestService>();
builder.Services.AddSingleton<IOllamaKernelService, OllamaKernelService>();
builder.Services.AddSingleton<ChatClient.Api.Services.McpFunctionIndexService>();
builder.Services.AddSingleton<AppForceLastUserReducer>();
builder.Services.AddSingleton<ChatClient.Api.Services.IAppChatHistoryBuilder, ChatClient.Api.Services.AppChatHistoryBuilder>();
builder.Services.AddScoped<ChatClient.Api.Services.OllamaServerAvailabilityService>();
builder.Services.AddSingleton<ChatClient.Shared.Services.IAgentDescriptionService, ChatClient.Api.Services.AgentDescriptionService>();
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<JsonFileRepository<UserSettings>>>();
    var filePath = config["UserSettings:FilePath"] ?? FilePathConstants.DefaultUserSettingsFile;
    return new JsonFileRepository<UserSettings>(filePath, logger);
});
builder.Services.AddSingleton<ChatClient.Shared.Services.IUserSettingsService, ChatClient.Api.Services.UserSettingsService>();
builder.Services.AddSingleton<ChatClient.Shared.Services.ISavedChatService, ChatClient.Api.Services.SavedChatService>();
builder.Services.AddSingleton<ChatClient.Shared.Services.IRagFileService, RagFileService>();
builder.Services.AddSingleton<ChatClient.Shared.Services.IRagVectorIndexService, RagVectorIndexService>();
builder.Services.AddSingleton<RagVectorIndexBackgroundService>();
builder.Services.AddSingleton<ChatClient.Shared.Services.IRagVectorIndexBackgroundService>(sp => sp.GetRequiredService<RagVectorIndexBackgroundService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<RagVectorIndexBackgroundService>());
builder.Services.AddSingleton<InMemoryVectorStore>();
builder.Services.AddSingleton<ChatClient.Shared.Services.IRagVectorSearchService, RagVectorSearchService>();
builder.Services.AddSingleton<ChatClient.Api.Services.IFileConverter, ChatClient.Api.Services.NoOpFileConverter>();
builder.Services.AddScoped<ChatClient.Api.Client.Services.IAppChatService, ChatClient.Api.Client.Services.AppChatService>();
builder.Services.AddScoped<ChatClient.Api.Client.Services.IChatViewModelService, ChatClient.Api.Client.Services.ChatViewModelService>();
builder.Services.AddSingleton<ChatClient.Api.Client.Services.IStopAgentFactory, ChatClient.Api.Client.Services.StopAgentFactory>();
builder.Services.AddSingleton<IChatFormatter, TextChatFormatter>();
builder.Services.AddSingleton<IChatFormatter, MarkdownChatFormatter>();
builder.Services.AddSingleton<IChatFormatter, HtmlChatFormatter>();

builder.Services.AddSingleton<AgentDescriptionSeeder>();
builder.Services.AddSingleton<LlmServerConfigSeeder>();
builder.Services.AddSingleton<McpServerConfigSeeder>();

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddHttpClient();

builder.Services.AddMudServices();

builder.Services.AddSingleton<CircuitHandler, AutoShutdownCircuitHandler>();

builder.Services.AddControllers(options =>
{
    options.Filters.Add<ApiExceptionFilter>();
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var agentSeeder = scope.ServiceProvider.GetRequiredService<AgentDescriptionSeeder>();
    await agentSeeder.SeedAsync();
    var llmSeeder = scope.ServiceProvider.GetRequiredService<LlmServerConfigSeeder>();
    await llmSeeder.SeedAsync();
    var mcpSeeder = scope.ServiceProvider.GetRequiredService<McpServerConfigSeeder>();
    await mcpSeeder.SeedAsync();

    var kernelService = scope.ServiceProvider.GetRequiredService<KernelService>();
    var mcpClientService = scope.ServiceProvider.GetRequiredService<IMcpClientService>();
    kernelService.SetMcpClientService(mcpClientService);

    var startupChecker = scope.ServiceProvider.GetRequiredService<OllamaServerAvailabilityService>();
    var ollamaStatus = await startupChecker.CheckOllamaStatusAsync();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    if (ollamaStatus.IsAvailable)
    {
        var indexService = scope.ServiceProvider.GetRequiredService<McpFunctionIndexService>();
        await indexService.BuildIndexAsync();
        logger.LogInformation("Ollama is available and ready.");
    }
    else
    {
        logger.LogWarning("Ollama is not available - {Error}", ollamaStatus.ErrorMessage);
        logger.LogWarning("The application will start but Ollama functionality will be limited.");
        logger.LogWarning("Users will be redirected to the setup page when trying to use Ollama features.");
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();

app.UseRouting();

app.MapControllers();
app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.MapGet("/api", () => "ChatClient API is running! Use /api/chat endpoint for chat communication.");

app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        var server = app.Services.GetRequiredService<IServer>();
        var addressesFeature = server.Features.Get<IServerAddressesFeature>();

        if (addressesFeature != null && addressesFeature.Addresses.Any())
        {
            var httpAddr = addressesFeature.Addresses.FirstOrDefault(a => a.StartsWith("http://"));
            var httpsAddr = addressesFeature.Addresses.FirstOrDefault(a => a.StartsWith("https://"));

            var launchUrl = httpsAddr ?? httpAddr;
            if (launchUrl != null)
            {
                BrowserLaunchService.DisplayInfoAndLaunchBrowser(launchUrl, httpsAddr ?? "N/A");
            }


        }
        else
        {
            logger.LogWarning("No server addresses were found. Browser cannot be launched.");
        }
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error during application startup");
    }
});

await app.RunAsync();
