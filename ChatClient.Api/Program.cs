using ChatClient.Api;
using ChatClient.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Check if running as an executable (not in development environment)
var isDevelopment = builder.Environment.IsDevelopment();

// Default ports
var defaultHttpPort = 5149;
var defaultHttpsPort = 7184;

// Find available ports if necessary
var httpPort = PortService.FindAvailablePort(defaultHttpPort);
var httpsPort = PortService.FindAvailablePort(defaultHttpsPort);

// Store application URLs for later use
var httpUrl = $"http://localhost:{httpPort}";
var httpsUrl = $"https://localhost:{httpsPort}";

// Configure web server URLs for the application
if (!isDevelopment)
{
    // In production, override URLs with our dynamic ports
    builder.WebHost.UseUrls(httpUrl, httpsUrl);
}

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
builder.Services.AddSingleton<ChatClient.Api.Services.McpClientService>();
builder.Services.AddSingleton<ChatClient.Api.Services.KernelService>();
builder.Services.AddSingleton<ChatClient.Api.Services.OllamaService>();
builder.Services.AddSingleton<ChatClient.Shared.Services.ISystemPromptService, ChatClient.Api.Services.SystemPromptService>();
builder.Services.AddSingleton<ChatClient.Shared.Services.IUserSettingsService, ChatClient.Api.Services.UserSettingsService>();
builder.Services.AddSingleton<ChatClient.Shared.Services.IMcpServerConfigService, ChatClient.Api.Services.McpServerConfigService>();

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

// Map controllers
app.MapControllers();

// Map Razor Pages and Blazor WebAssembly
app.MapRazorPages();
app.MapFallbackToFile("index.html");

// Display info about the running application
app.MapGet("/api", () => $"ChatClient API is running on port {httpPort}! Use /api/chat endpoint for chat communication.");

// Prepare browser launch for after app initialization
if (!isDevelopment)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        Console.WriteLine("Application fully started, preparing to launch browser...");
        BrowserLaunchService.DisplayInfoAndLaunchBrowser(httpUrl, httpsUrl);
    });
}

// Print final startup message
Console.WriteLine($"Starting web server on {httpUrl} and {httpsUrl}...");
app.Run();
