using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;

namespace ChatClient.Api.Services;

public class KernelService(
    IConfiguration configuration,
    ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory,
    McpClientService mcpClientService,
    ILogger<KernelService> logger)
{
    private IMcpClient? _mcpClient;
    private IReadOnlyList<McpClientTool>? _mcpTools;

    public Kernel CreateKernel()
    {
        var baseUrl = configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
        var modelId = configuration["Ollama:Model"] ?? "phi4:14b";

        var httpClient = httpClientFactory.CreateClient("Ollama");
        httpClient.BaseAddress = new Uri(baseUrl);
        httpClient.Timeout = TimeSpan.FromMinutes(10);

        var builder = Kernel.CreateBuilder();
        builder.AddOllamaChatCompletion(
            modelId: modelId,
            httpClient: httpClient,
            serviceId: "ollama"
        );

        builder.Services.AddLogging(c => c.AddConsole().SetMinimumLevel(LogLevel.Information));
        builder.Services.AddSingleton(new PromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
        });

        var kernel = builder.Build();

        InitializeMcpIntegrationAsync(kernel).GetAwaiter().GetResult();

        return kernel;
    }

    private async Task InitializeMcpIntegrationAsync(Kernel kernel)
    {
        try
        {
            _mcpClient = await mcpClientService.CreateMcpClientAsync(kernel);
            _mcpTools = await mcpClientService.GetMcpTools(_mcpClient);

            if (_mcpTools.Count > 0)
            {
                logger.LogInformation("Registering {Count} MCP tools as kernel functions", _mcpTools.Count);

#pragma warning disable SKEXP0001
                var pluginFunctions = _mcpTools.Select(tool => tool.AsKernelFunction()).ToList();
                kernel.Plugins.AddFromFunctions("McpTools", pluginFunctions);
#pragma warning restore SKEXP0001

                logger.LogInformation("MCP tools registered successfully");
            }
            else
            {
                logger.LogWarning("No MCP tools available to register");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize MCP integration: {Message}", ex.Message);
        }
    }
}
