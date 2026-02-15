using ChatClient.Api;
using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Api.Services.Seed;
using ChatClient.Application.Services.Agentic;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using System.IO;
using System.Text;

// Enable UTF-8 for proper Cyrillic support.
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

if (await BuiltInMcpServerHost.TryRunAsync(args))
{
    return;
}

var runFromSelfContainedLayout = !string.Equals(
    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
    Environments.Development,
    StringComparison.OrdinalIgnoreCase);
var appBaseDirectory = ResolveApplicationBaseDirectory();

if (runFromSelfContainedLayout)
{
    // Winget portable installs can be launched from any working directory.
    // Pin the process cwd to the executable folder so relative paths stay stable.
    Directory.SetCurrentDirectory(appBaseDirectory);
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("ModelContextProtocol", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Extensions.Http", LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File("Logs/ollamachat-.log", rollingInterval: RollingInterval.Day, fileSizeLimitBytes: 10_000_000, rollOnFileSizeLimit: true, retainedFileCountLimit: 5)
    .WriteTo.Debug()
    .CreateLogger();

var builder = runFromSelfContainedLayout
    ? WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = appBaseDirectory,
        WebRootPath = Path.Combine(appBaseDirectory, "wwwroot")
    })
    : WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();
builder.Services.Configure<ChatEngineOptions>(builder.Configuration.GetSection(ChatEngineOptions.SectionName));

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>(optional: true);
}

builder.Services.AddApplicationServices(builder.Configuration);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var agentSeeder = scope.ServiceProvider.GetRequiredService<AgentDescriptionSeeder>();
    await agentSeeder.SeedAsync();
    var llmSeeder = scope.ServiceProvider.GetRequiredService<LlmServerConfigSeeder>();
    await llmSeeder.SeedAsync();
    var mcpSeeder = scope.ServiceProvider.GetRequiredService<McpServerConfigSeeder>();
    await mcpSeeder.SeedAsync();
    var ragFilesSeeder = scope.ServiceProvider.GetRequiredService<RagFilesSeeder>();
    await ragFilesSeeder.SeedAsync();

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
        logger.LogWarning("Ollama features are limited. Open '/llm-servers' to configure server access.");
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

static string ResolveApplicationBaseDirectory()
{
    var processPath = Environment.ProcessPath;
    if (string.IsNullOrWhiteSpace(processPath))
    {
        return AppContext.BaseDirectory;
    }

    try
    {
        var processFile = new FileInfo(processPath);
        var targetFile = processFile.ResolveLinkTarget(returnFinalTarget: true);
        if (targetFile is FileInfo resolvedFile && !string.IsNullOrWhiteSpace(resolvedFile.DirectoryName))
        {
            return resolvedFile.DirectoryName;
        }
    }
    catch
    {
        // Keep fallback path when link target cannot be resolved.
    }

    var processDirectory = Path.GetDirectoryName(processPath);
    return string.IsNullOrWhiteSpace(processDirectory)
        ? AppContext.BaseDirectory
        : processDirectory;
}
