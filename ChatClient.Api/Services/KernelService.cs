using ChatClient.Shared.Models;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;

namespace ChatClient.Api.Services;

public class KernelService(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    McpClientService mcpClientService,
    ILogger<KernelService> logger)
{
    private IMcpClient? _mcpClient;
    private IReadOnlyList<McpClientTool>? _mcpTools;

    public async Task<Kernel> CreateKernelAsync(string modelId, IEnumerable<string>? functionNames = null)
    {
        var baseUrl = configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
        var httpClient = httpClientFactory.CreateClient("DefaultClient");
        httpClient.BaseAddress = new Uri(baseUrl);

        IKernelBuilder builder = Kernel.CreateBuilder();
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

        // Load MCP tools
        await InitializeMcpIntegrationAsync(kernel);

        // Register selected MCP tools as kernel functions
        if (_mcpTools != null && _mcpTools.Count > 0)
        {
            // If functionNames is null, register all tools; if empty list, register none; otherwise register specified
            List<McpClientTool> toolsToRegister;
            if (functionNames == null)
            {
                toolsToRegister = _mcpTools.ToList();
            }
            else
            {
                toolsToRegister = _mcpTools.Where(t => functionNames.Contains(t.Name)).ToList();
            }
            if (toolsToRegister.Count > 0)
            {
                var pluginFunctions = toolsToRegister.Select(tool => tool.AsKernelFunction()).ToList();
                kernel.Plugins.AddFromFunctions("McpTools", pluginFunctions);
                logger.LogInformation("Registered {Count} MCP tools based on selection", pluginFunctions.Count);
            }
            else
            {
                logger.LogWarning("No MCP tools registered because none were selected");
            }
        }
        else
        {
            logger.LogWarning("No MCP tools loaded to register");
        }

        return kernel;
    }

    private async Task InitializeMcpIntegrationAsync(Kernel kernel)
    {
        try
        {
            _mcpClient = await mcpClientService.CreateMcpClientAsync(kernel);
            if (_mcpClient == null) return;

            _mcpTools = await mcpClientService.GetMcpTools(_mcpClient);
            if (_mcpTools.Count > 0)
            {
                logger.LogInformation("Loaded {Count} MCP tools", _mcpTools.Count);
            }
            else
            {
                logger.LogWarning("No MCP tools available to load");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize MCP integration: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Returns the list of available functions registered in the kernel.
    /// </summary>
    public IEnumerable<FunctionInfo> GetAvailableFunctions()
    {
        return _mcpTools?
            .Select(tool => new FunctionInfo { Name = tool.Name, Description = tool.Description })
            ?? Enumerable.Empty<FunctionInfo>();
    }
}
