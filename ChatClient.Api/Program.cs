using ChatClient.Api;
using ChatClient.Api.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Check if running as an executable (not in development environment)
var isDevelopment = builder.Environment.IsDevelopment();

// Configure Kestrel to use dynamic ports (port 0 means "any available port")
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(System.Net.IPAddress.Parse("127.0.0.1"), 0);        // HTTP on any available port
    options.Listen(System.Net.IPAddress.Parse("127.0.0.1"), 0, listenOptions =>
    {
        listenOptions.UseHttps();      // HTTPS on another available port
    });
});

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

// Add controllers with API support
builder.Services.AddControllers(options =>
{
    options.Filters.Add<ApiExceptionFilter>();
});

// Register services
builder.Services.AddSingleton<ChatClient.Shared.Services.IMcpServerConfigService, ChatClient.Api.Services.McpServerConfigService>();
builder.Services.AddSingleton<ChatClient.Api.Services.McpClientService>();
builder.Services.AddSingleton<ChatClient.Api.Services.KernelService>();
builder.Services.AddSingleton<ChatClient.Api.Services.OllamaService>();
builder.Services.AddSingleton<ChatClient.Shared.Services.ISystemPromptService, ChatClient.Api.Services.SystemPromptService>();
builder.Services.AddSingleton<ChatClient.Shared.Services.IUserSettingsService, ChatClient.Api.Services.UserSettingsService>();

// Add controllers with JSON options
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// Configure Blazor WebAssembly hosting
builder.Services.AddRazorPages();
builder.Services.AddControllersWithViews();

var app = builder.Build();

// Setup middleware pipeline
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Static files for Blazor client
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseRouting();

// Map controllers and endpoints
app.MapControllers();
app.MapRazorPages();

// Ensure all routes not matched will fall back to the index.html file
app.MapFallbackToFile("index.html");

// Map a simple API info endpoint
app.MapGet("/api", () => "ChatClient API is running! Use /api/chat endpoint for chat communication.");

// Register callback to run after application is fully initialized
app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        // Access the server to get the actual assigned addresses
        var server = app.Services.GetRequiredService<IServer>();
        var addressesFeature = server.Features.Get<IServerAddressesFeature>();
        //Debugger.Launch();
        if (addressesFeature != null && addressesFeature.Addresses.Any())
        {
            // Get the actual assigned addresses
            var httpAddr = addressesFeature.Addresses.First(a => a.StartsWith("http://"));
            var httpsAddr = addressesFeature.Addresses.First(a => a.StartsWith("https://"));

            // Log the addresses we found
            Console.WriteLine($"Server listening on: {string.Join(", ", addressesFeature.Addresses)}");
            
            // Launch browser with the correct dynamic port
            Console.WriteLine("Application fully started, launching browser...");
            BrowserLaunchService.DisplayInfoAndLaunchBrowser(httpAddr, httpsAddr);
            
            // Log that wwwroot directory is being served from
            var env = app.Services.GetService<IWebHostEnvironment>();
            Console.WriteLine($"Serving static files from: {env?.ContentRootPath}");
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

// Print startup message
Console.WriteLine("Starting web server on dynamic ports...");
app.Run();
