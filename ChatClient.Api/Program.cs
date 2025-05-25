using ChatClient.Api;
using ChatClient.Api.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using MudBlazor.Services;
using System.Reflection;

var exeFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
var webRootPath = Path.Combine(exeFolder, "wwwroot");

Directory.SetCurrentDirectory(exeFolder);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = exeFolder,
    WebRootPath = webRootPath,
});

Console.WriteLine($"Executable folder: {exeFolder}");
Console.WriteLine($"Web root: {builder.Environment.WebRootPath}");
Console.WriteLine($"[STARTUP] Initial WebRootPath: {builder.Environment.WebRootPath}");

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
builder.Services.AddSingleton<ChatClient.Api.Services.KernelService>();
builder.Services.AddSingleton<ChatClient.Api.Services.OllamaService>();
builder.Services.AddSingleton<ChatClient.Shared.Services.ISystemPromptService, ChatClient.Api.Services.SystemPromptService>();
builder.Services.AddSingleton<ChatClient.Shared.Services.IUserSettingsService, ChatClient.Api.Services.UserSettingsService>();

// Add controllers with JSON options
builder.Services.AddControllers(options =>
{
    options.Filters.Add<ApiExceptionFilter>();
}).AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});


// Configure Blazor Server hosting
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// Add HttpContextAccessor for Blazor Server
builder.Services.AddHttpContextAccessor();

// Add MudBlazor services
builder.Services.AddMudServices();

// Add HttpClient with default base address for development
builder.Services.AddScoped(sp =>
{
    var httpContextAccessor = sp.GetService<IHttpContextAccessor>();
    var httpClient = new HttpClient();

    if (httpContextAccessor?.HttpContext != null)
    {
        var request = httpContextAccessor.HttpContext.Request;
        httpClient.BaseAddress = new Uri($"{request.Scheme}://{request.Host}");
    }
    else
    {
        // Fallback for development
        httpClient.BaseAddress = new Uri("http://localhost:5000");
    }

    return httpClient;
});

// Add Client services for Blazor Server
builder.Services.AddScoped<ChatClient.Api.Client.Services.IChatService, ChatClient.Api.Client.Services.ChatService>();
builder.Services.AddScoped<ChatClient.Api.Client.Services.IChatViewModelService, ChatClient.Api.Client.Services.ChatViewModelService>();
builder.Services.AddScoped<ChatClient.Api.Client.Services.IModelsService, ChatClient.Api.Client.Services.ModelsService>();
builder.Services.AddScoped<ChatClient.Api.Client.Services.ClientSystemPromptService>();
builder.Services.AddScoped<ChatClient.Api.Client.Services.ClientMcpServerConfigService>();
builder.Services.AddScoped<ChatClient.Api.Client.Services.ClientUserSettingsService>();

var app = builder.Build();
Console.WriteLine($"[AFTER BUILD] WebRootPath: {app.Environment.WebRootPath}");

// Force set the WebRootPath if it's incorrect
if (app.Environment.WebRootPath != webRootPath)
{
    Console.WriteLine($"[WARNING] WebRootPath mismatch! Expected: {webRootPath}, Actual: {app.Environment.WebRootPath}");
    Console.WriteLine($"[FIX] Attempting to correct WebRootPath...");

    // This is a workaround to force the correct WebRootPath
    var environmentType = app.Environment.GetType();
    var webRootPathProperty = environmentType.GetProperty("WebRootPath");
    if (webRootPathProperty != null && webRootPathProperty.CanWrite)
    {
        webRootPathProperty.SetValue(app.Environment, webRootPath);
        Console.WriteLine($"[FIX] WebRootPath corrected to: {app.Environment.WebRootPath}");
    }
}

// Setup middleware pipeline
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    Console.WriteLine("Development mode: Using developer exception page");
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    Console.WriteLine("Production mode: Using explicit static files configuration");
}

Console.WriteLine($"[AFTER EXCEPTION HANDLING] WebRootPath: {app.Environment.WebRootPath}");

// Static files for Blazor Server
// Explicitly configure static files to use the correct wwwroot path
var staticFileOptions = new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRootPath),
    RequestPath = ""
};
Console.WriteLine($"[BEFORE UseStaticFiles] Configuring static files with path: {webRootPath}");
Console.WriteLine($"[BEFORE UseStaticFiles] Static file provider path: {staticFileOptions.FileProvider.GetDirectoryContents("").Exists}");
Console.WriteLine($"[BEFORE UseStaticFiles] Current directory: {Directory.GetCurrentDirectory()}");
Console.WriteLine($"[BEFORE UseStaticFiles] WebRootPath: {app.Environment.WebRootPath}");
Console.WriteLine($"[BEFORE UseStaticFiles] ContentRootPath: {app.Environment.ContentRootPath}");

app.UseStaticFiles(staticFileOptions);

Console.WriteLine($"[AFTER UseStaticFiles] WebRootPath: {app.Environment.WebRootPath}");
Console.WriteLine($"[AFTER UseStaticFiles] Current directory: {Directory.GetCurrentDirectory()}");
Console.WriteLine($"[AFTER UseStaticFiles] ContentRootPath: {app.Environment.ContentRootPath}");

app.UseRouting();
Console.WriteLine($"[AFTER UseRouting] WebRootPath: {app.Environment.WebRootPath}");
Console.WriteLine($"[AFTER UseRouting] Current directory: {Directory.GetCurrentDirectory()}");
Console.WriteLine($"[AFTER UseRouting] ContentRootPath: {app.Environment.ContentRootPath}");

// Map controllers and endpoints
app.MapControllers();
Console.WriteLine($"[AFTER MapControllers] WebRootPath: {app.Environment.WebRootPath}");
Console.WriteLine($"[AFTER MapControllers] Current directory: {Directory.GetCurrentDirectory()}");
Console.WriteLine($"[AFTER MapControllers] ContentRootPath: {app.Environment.ContentRootPath}");

app.MapRazorPages();
Console.WriteLine($"[AFTER MapRazorPages] WebRootPath: {app.Environment.WebRootPath}");
Console.WriteLine($"[AFTER MapRazorPages] Current directory: {Directory.GetCurrentDirectory()}");
Console.WriteLine($"[AFTER MapRazorPages] ContentRootPath: {app.Environment.ContentRootPath}");

app.MapBlazorHub();
Console.WriteLine($"[AFTER MapBlazorHub] WebRootPath: {app.Environment.WebRootPath}");
Console.WriteLine($"[AFTER MapBlazorHub] Current directory: {Directory.GetCurrentDirectory()}");
Console.WriteLine($"[AFTER MapBlazorHub] ContentRootPath: {app.Environment.ContentRootPath}");

// Map the Blazor app
app.MapFallbackToPage("/_Host");
Console.WriteLine($"[AFTER MapFallbackToPage] WebRootPath: {app.Environment.WebRootPath}");
Console.WriteLine($"[AFTER MapFallbackToPage] Current directory: {Directory.GetCurrentDirectory()}");
Console.WriteLine($"[AFTER MapFallbackToPage] ContentRootPath: {app.Environment.ContentRootPath}");

// Map a simple API info endpoint
app.MapGet("/api", () => "ChatClient API is running! Use /api/chat endpoint for chat communication.");

// CRITICAL FIX: Add additional static files middleware after all mappings
// This ensures static files are served correctly even if other middlewares interfere
Console.WriteLine($"[BEFORE FINAL UseStaticFiles] Adding final static files middleware");
Console.WriteLine($"[BEFORE FINAL UseStaticFiles] webRootPath variable: {webRootPath}");
Console.WriteLine($"[BEFORE FINAL UseStaticFiles] Current directory: {Directory.GetCurrentDirectory()}");

// Add one more static files middleware as a safety net with explicit path mapping
var finalStaticFileOptions = new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRootPath),
    RequestPath = "",
    ServeUnknownFileTypes = false
};
app.UseStaticFiles(finalStaticFileOptions);

Console.WriteLine($"[AFTER FINAL UseStaticFiles] Final static files middleware added");

// Register callback to run after application is fully initialized
app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        // Access the server to get the actual assigned addresses
        var server = app.Services.GetRequiredService<IServer>();
        var addressesFeature = server.Features.Get<IServerAddressesFeature>();

        if (addressesFeature != null && addressesFeature.Addresses.Any())
        {
            // Get the actual assigned addresses
            var httpAddr = addressesFeature.Addresses.FirstOrDefault(a => a.StartsWith("http://"));
            var httpsAddr = addressesFeature.Addresses.FirstOrDefault(a => a.StartsWith("https://"));

            // Log the addresses we found
            Console.WriteLine($"Server listening on: {string.Join(", ", addressesFeature.Addresses)}");

            // Launch browser with the correct dynamic port
            Console.WriteLine("Application fully started, launching browser...");

            // Use HTTPS if available, otherwise HTTP
            var launchUrl = httpsAddr ?? httpAddr;
            if (launchUrl != null)
            {
                BrowserLaunchService.DisplayInfoAndLaunchBrowser(launchUrl, httpsAddr ?? "N/A");
            }              // Log that wwwroot directory is being served from
            var env = app.Services.GetService<IWebHostEnvironment>();
            Console.WriteLine($"Content root path: {env?.ContentRootPath}");
            Console.WriteLine($"Web root path: {env?.WebRootPath}");
            Console.WriteLine($"[FINAL DIAGNOSTICS] Current directory: {Directory.GetCurrentDirectory()}");
            Console.WriteLine($"[FINAL DIAGNOSTICS] Environment.CurrentDirectory: {Environment.CurrentDirectory}");
            Console.WriteLine($"[FINAL DIAGNOSTICS] Assembly location: {Assembly.GetExecutingAssembly().Location}");
            Console.WriteLine($"[FINAL DIAGNOSTICS] Process working directory: {Environment.CurrentDirectory}");
            Console.WriteLine($"[FINAL DIAGNOSTICS] Original webRootPath variable: {webRootPath}");
            Console.WriteLine($"[FINAL DIAGNOSTICS] Expected exe folder: {exeFolder}");

            // Check if the configured wwwroot actually exists and has content
            if (env?.WebRootPath != null)
            {
                Console.WriteLine($"[FINAL DIAGNOSTICS] Configured WebRootPath exists: {Directory.Exists(env.WebRootPath)}");
                if (Directory.Exists(env.WebRootPath))
                {
                    var rootFiles = Directory.GetFiles(env.WebRootPath, "*", SearchOption.TopDirectoryOnly);
                    Console.WriteLine($"[FINAL DIAGNOSTICS] Files in WebRoot: {rootFiles.Length}");
                    Console.WriteLine($"[FINAL DIAGNOSTICS] Sample files: {string.Join(", ", rootFiles.Take(5).Select(Path.GetFileName))}");
                }

                // Also check our expected wwwroot
                Console.WriteLine($"[FINAL DIAGNOSTICS] Expected webRootPath ({webRootPath}) exists: {Directory.Exists(webRootPath)}");
                if (Directory.Exists(webRootPath))
                {
                    var expectedFiles = Directory.GetFiles(webRootPath, "*", SearchOption.TopDirectoryOnly);
                    Console.WriteLine($"[FINAL DIAGNOSTICS] Files in expected WebRoot: {expectedFiles.Length}");
                    Console.WriteLine($"[FINAL DIAGNOSTICS] Expected sample files: {string.Join(", ", expectedFiles.Take(5).Select(Path.GetFileName))}");
                }
            }

            // Check for potential directory changes
            var currentWorkingDir = Directory.GetCurrentDirectory();
            Console.WriteLine($"[FINAL DIAGNOSTICS] Working directory changed: {currentWorkingDir != exeFolder}");
            if (currentWorkingDir != exeFolder)
            {
                Console.WriteLine($"[WARNING] Working directory has changed from {exeFolder} to {currentWorkingDir}");
                Console.WriteLine($"[FIX ATTEMPT] Trying to restore working directory...");
                try
                {
                    Directory.SetCurrentDirectory(exeFolder);
                    Console.WriteLine($"[FIX SUCCESS] Working directory restored to: {Directory.GetCurrentDirectory()}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FIX FAILED] Could not restore working directory: {ex.Message}");
                }
            }
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
