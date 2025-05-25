using ChatClient.Api;

var builder = WebApplication.CreateBuilder(args);


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

// Add CORS services (not needed when hosting Blazor client in the same project)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowBlazorClient", builder =>
    {
        builder.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Add Razor Pages and Blazor WebAssembly server-side support
builder.Services.AddRazorPages();

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

var app = builder.Build();

app.UseCors("AllowBlazorClient");

// Static files for Blazor client
app.UseStaticFiles();
app.UseRouting();
app.UseBlazorFrameworkFiles(); // Add Blazor framework file middleware

// API endpoints under /api path
app.MapControllers();

// Map Blazor WebAssembly entry point
app.MapFallbackToFile("index.html");

// Let the API handle root requests as well
app.MapGet("/api", () => "ChatClient API is running! Use /api/chat endpoint for chat communication.");

app.Run();
