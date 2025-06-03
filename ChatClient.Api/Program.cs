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

// Configure default HttpClient factory with named clients
builder.Services.AddHttpClient("DefaultClient", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
});

builder.Services.AddHttpClient("OllamaClient", client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
});

var loggerFactory = LoggerFactory.Create(logging =>
{
    logging.AddConsole();
    logging.AddDebug();
});
builder.Services.AddSingleton<ILoggerFactory>(loggerFactory);

// Register services
builder.Services.AddSingleton<ChatClient.Shared.Services.IMcpServerConfigService, ChatClient.Api.Services.McpServerConfigService>();
builder.Services.AddSingleton<ChatClient.Api.Services.McpClientService>();
builder.Services.AddSingleton<ChatClient.Api.Services.McpSamplingService>();
builder.Services.AddSingleton<ChatClient.Api.Services.KernelService>();
builder.Services.AddSingleton<ChatClient.Api.Services.OllamaService>();
builder.Services.AddSingleton<ChatClient.Shared.Services.ISystemPromptService, ChatClient.Api.Services.SystemPromptService>();
builder.Services.AddSingleton<ChatClient.Shared.Services.IUserSettingsService, ChatClient.Api.Services.UserSettingsService>();

// Register client services for Blazor components  
builder.Services.AddScoped<ChatClient.Api.Client.Services.IFunctionsService, ChatClient.Api.Client.Services.FunctionsService>();
builder.Services.AddScoped<ChatClient.Api.Client.Services.IModelsService, ChatClient.Api.Client.Services.ModelsService>();
builder.Services.AddScoped<ChatClient.Api.Client.Services.IChatService, ChatClient.Api.Client.Services.ChatService>();
builder.Services.AddScoped<ChatClient.Api.Client.Services.IChatViewModelService, ChatClient.Api.Client.Services.ChatViewModelService>();

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddControllers();

// Register MudBlazor services
builder.Services.AddMudServices();

// Register API exception filter
builder.Services.AddControllers(options =>
{
    options.Filters.Add<ApiExceptionFilter>();
});

var app = builder.Build();

// Resolve circular dependency by injecting McpClientService into KernelService
using (var scope = app.Services.CreateScope())
{
    var kernelService = scope.ServiceProvider.GetRequiredService<KernelService>();
    var mcpClientService = scope.ServiceProvider.GetRequiredService<McpClientService>();
    kernelService.SetMcpClientService(mcpClientService);
}

// Configure the HTTP request pipeline.
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

// Configure static files
app.UseStaticFiles();

app.UseRouting();

// Map controllers and Blazor components
app.MapControllers();
app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

// Simple API info endpoint
app.MapGet("/api", () => "ChatClient API is running! Use /api/chat endpoint for chat communication.");

// Register callback to run after application is fully initialized
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
app.Run();
