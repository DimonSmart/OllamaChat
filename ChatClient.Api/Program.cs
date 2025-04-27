using ChatClient.Api;
using ChatClient.Api.Services;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddHttpClient();
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
builder.Services.AddSingleton<ChatClient.Shared.Services.ISystemPromptService, ChatClient.Api.Services.SystemPromptService>();

// Register Kernel as a singleton
builder.Services.AddSingleton(sp =>
{
    var kernelService = sp.GetRequiredService<ChatClient.Api.Services.KernelService>();
    return kernelService.CreateKernel();
});

// Register chat service using the kernel
builder.Services.AddSingleton<IChatCompletionService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var kernel = sp.GetRequiredService<Kernel>();
    return kernel.GetRequiredService<IChatCompletionService>();
});

// Add controllers with JSON options
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();

// Configure the HTTP request pipeline
app.UseCors("AllowBlazorClient");

// Map endpoints
app.MapControllers();

app.MapGet("/", () => "ChatClient API is running! Use /api/chat endpoint for chat communication.");

app.Run();