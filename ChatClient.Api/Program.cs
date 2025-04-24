using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol.Transport;
using ChatClient.Shared.Models;
using Microsoft.SemanticKernel.Connectors.Ollama;
using System.Diagnostics.CodeAnalysis;
using ChatClient.Api.Models;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.TextGeneration;
using ChatClient.Api.Services;

// Suppress the experimental API warning
[assembly: SuppressMessage("SemanticKernel", "SKEXP0070", Justification = "Using experimental API as required")]

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLogging();
builder.Services.AddHttpClient();

// Create logger factory
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

// Configure MCP clients
try
{
    var mcpConfigs = builder.Configuration.GetSection("McpServers").Get<List<ChatClient.Api.Models.McpServerConfig>>();
    
    if (mcpConfigs != null && mcpConfigs.Count > 0)
    {
        foreach (var cfg in mcpConfigs)
        {
            builder.Services.AddSingleton<IMcpClient>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<Program>>();
                logger.LogInformation($"Initializing MCP client: {cfg.Name}, command: {cfg.Command}");
                
                var transport = new StdioClientTransport(
                    new StdioClientTransportOptions
                    {
                        Command = cfg.Command ?? "echo",
                        Arguments = cfg.Arguments ?? Array.Empty<string>()
                    },
                    sp.GetRequiredService<ILoggerFactory>()
                );
                return McpClientFactory.CreateAsync(transport).GetAwaiter().GetResult();
            });
        }
    }
    else
    {
        builder.Services.AddSingleton<IMcpClient>(sp => {
            var logger = sp.GetRequiredService<ILogger<Program>>();
            logger.LogWarning("MCP configuration missing, using stub");
            var transport = new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Command = "echo",
                    Arguments = new[] {"MCP client stub"}
                },
                sp.GetRequiredService<ILoggerFactory>()
            );
            return McpClientFactory.CreateAsync(transport).GetAwaiter().GetResult();
        });
    }
}
catch (Exception ex)
{
    builder.Services.AddSingleton<IMcpClient>(sp => {
        var logger = sp.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error configuring MCP clients, using stub");
        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Command = "echo",
                Arguments = new[] {"MCP client stub"}
            },
            sp.GetRequiredService<ILoggerFactory>()
        );
        return McpClientFactory.CreateAsync(transport).GetAwaiter().GetResult();
    });
}

// Register KernelService
builder.Services.AddSingleton<KernelService>();

// Register Kernel as a singleton
builder.Services.AddSingleton(sp => {
    var kernelService = sp.GetRequiredService<KernelService>();
    return kernelService.CreateKernel();
});

// Register chat service using the kernel
builder.Services.AddSingleton<IChatCompletionService>(sp => {
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var kernel = sp.GetRequiredService<Kernel>();
    return kernel.GetRequiredService<IChatCompletionService>();
});

// Add controllers with JSON options
builder.Services.AddControllers().AddJsonOptions(options => {
    options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();

// Configure the HTTP request pipeline
app.UseCors("AllowBlazorClient");

// Map endpoints
app.MapControllers();

app.MapGet("/", () => "ChatClient API is running! Use /api/chat endpoint for chat communication.");

app.Run();