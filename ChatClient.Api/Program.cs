using ChatClient.Api;
using ChatClient.Api.Services;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// For single-file deployment compatibility only in production
if (builder.Environment.IsProduction())
{
    var exeFolder = AppContext.BaseDirectory;
    var webRootPath = Path.Combine(exeFolder, "wwwroot");
    Directory.SetCurrentDirectory(exeFolder);
    builder.Environment.WebRootPath = webRootPath;
    builder.Environment.ContentRootPath = exeFolder;
}

Console.WriteLine($"Environment: {builder.Environment.EnvironmentName}");
Console.WriteLine($"Content Root: {builder.Environment.ContentRootPath}");
Console.WriteLine($"Web Root: {builder.Environment.WebRootPath}");

var loggerFactory = LoggerFactory.Create(logging =>
{
    logging.AddConsole();
    logging.AddDebug();
});
builder.Services.AddSingleton<ILoggerFactory>(loggerFactory);

builder.Services.AddSingleton<ChatClient.Shared.Services.IMcpServerConfigService, ChatClient.Api.Services.McpServerConfigService>();
builder.Services.AddSingleton<ChatClient.Api.Services.IMcpClientService, ChatClient.Api.Services.McpClientService>();
builder.Services.AddSingleton<ChatClient.Api.Services.McpSamplingService>();
builder.Services.AddSingleton<ChatClient.Api.Services.KernelService>();
builder.Services.AddSingleton<ChatClient.Api.Services.IOllamaClientService, ChatClient.Api.Services.OllamaService>();
builder.Services.AddSingleton<ChatClient.Api.Services.McpFunctionIndexService>();
builder.Services.AddSingleton<ChatClient.Api.Services.IChatHistoryBuilder, ChatClient.Api.Services.ChatHistoryBuilder>();
builder.Services.AddScoped<ChatClient.Api.Services.StartupOllamaChecker>();
builder.Services.AddSingleton<ChatClient.Shared.Services.ISystemPromptService, ChatClient.Api.Services.SystemPromptService>();
builder.Services.AddSingleton<ChatClient.Shared.Services.IUserSettingsService, ChatClient.Api.Services.UserSettingsService>();

builder.Services.AddScoped<ChatClient.Shared.LlmAgents.ILlmAgent>(sp => new ChatClient.Api.Services.KernelLlmAgent("Default"));
builder.Services.AddScoped<ChatClient.Shared.LlmAgents.ILlmAgentCoordinator, ChatClient.Api.Services.DefaultLlmAgentCoordinator>();

builder.Services.AddScoped<ChatClient.Api.Client.Services.IChatService, ChatClient.Api.Client.Services.ChatService>();
builder.Services.AddScoped<ChatClient.Api.Client.Services.IChatViewModelService, ChatClient.Api.Client.Services.ChatViewModelService>();

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

builder.Services.AddMudServices();

builder.Services.AddControllers(options =>
{
    options.Filters.Add<ApiExceptionFilter>();
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var kernelService = scope.ServiceProvider.GetRequiredService<KernelService>();
    var mcpClientService = scope.ServiceProvider.GetRequiredService<IMcpClientService>();
    kernelService.SetMcpClientService(mcpClientService);
    var indexService = scope.ServiceProvider.GetRequiredService<McpFunctionIndexService>();
    await indexService.BuildIndexAsync();

    // Check Ollama status at startup
    var startupChecker = scope.ServiceProvider.GetRequiredService<StartupOllamaChecker>();
    var ollamaStatus = await startupChecker.CheckOllamaStatusAsync();

    if (!ollamaStatus.IsAvailable)
    {
        Console.WriteLine($"Warning: Ollama is not available - {ollamaStatus.ErrorMessage}");
        Console.WriteLine("The application will start but Ollama functionality will be limited.");
        Console.WriteLine("Users will be redirected to the setup page when trying to use Ollama features.");
    }
    else
    {
        Console.WriteLine("Ollama is available and ready.");
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    Console.WriteLine("Development mode enabled");
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    Console.WriteLine("Production mode enabled");
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
        var server = app.Services.GetRequiredService<IServer>();
        var addressesFeature = server.Features.Get<IServerAddressesFeature>();

        if (addressesFeature != null && addressesFeature.Addresses.Any())
        {
            var httpAddr = addressesFeature.Addresses.FirstOrDefault(a => a.StartsWith("http://"));
            var httpsAddr = addressesFeature.Addresses.FirstOrDefault(a => a.StartsWith("https://"));

            Console.WriteLine($"Server listening on: {string.Join(", ", addressesFeature.Addresses)}");

            var launchUrl = httpsAddr ?? httpAddr;
            if (launchUrl != null)
            {
                BrowserLaunchService.DisplayInfoAndLaunchBrowser(launchUrl, httpsAddr ?? "N/A");
            }

            // Log environment info
            var env = app.Services.GetService<IWebHostEnvironment>();
            Console.WriteLine($"Content root path: {env?.ContentRootPath}");
            Console.WriteLine($"Web root path: {env?.WebRootPath}");
        }
        else
        {
            Console.WriteLine("Warning: No server addresses were found. Browser cannot be launched.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error during application startup: {ex.Message}");
    }
});

Console.WriteLine("Starting web server...");
await app.RunAsync();
