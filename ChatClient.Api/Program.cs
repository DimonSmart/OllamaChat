using ChatClient.Api;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

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

// Add CORS services
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowBlazorClient", builder =>
    {
        builder.WithOrigins("https://localhost:7190", "http://localhost:5270")
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

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

app.MapControllers();

app.MapGet("/", () => "ChatClient API is running! Use /api/chat endpoint for chat communication.");

app.Run();
