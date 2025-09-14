using ChatClient.Api;
using ChatClient.Api.Services;
using ChatClient.Api.Services.Seed;
using ChatClient.Api.Client.Services.Reducers;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;
using Serilog;
using System.IO;
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
builder.Services.AddSingleton<AppForceLastUserReducer>();
builder.Services.AddSingleton<ThinkTagsRemovalReducer>();
builder.Services.AddSingleton<IChatHistoryReducer>(sp =>
{
    var thinkTagsReducer = sp.GetRequiredService<ThinkTagsRemovalReducer>();
    var forceLastUserReducer = sp.GetRequiredService<AppForceLastUserReducer>();
    return new MetaReducer([thinkTagsReducer, forceLastUserReducer]);
});
builder.Services.AddApplicationServices();

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
