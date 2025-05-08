using ChatClient.Shared.Models;
using Microsoft.SemanticKernel;

namespace ChatClient.Api.Services;

public class KernelService(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    McpClientService mcpClientService,
    ILogger<KernelService> logger)
{
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

        // Register selected MCP tools as kernel functions
        if (functionNames != null && functionNames.Any())
        {
            await RegisterMcpToolsAsync(kernel, functionNames);
        }

        return kernel;
    }

    private async Task RegisterMcpToolsAsync(Kernel kernel, IEnumerable<string> functionNames)
    {
        try
        {
            var mcpClient = await mcpClientService.CreateMcpClientAsync();
            if (mcpClient == null)
            {
                logger.LogWarning("MCP client could not be created");
                return;
            }

            var mcpTools = await mcpClientService.GetMcpTools(mcpClient);
            if (mcpTools.Count == 0)
            {
                logger.LogWarning("No MCP tools available to register");
                return;
            }

            var toolsToRegister = mcpTools.Where(t => functionNames.Contains(t.Name)).ToList();
            if (toolsToRegister.Count == 0)
            {
                logger.LogWarning("No MCP tools matched the requested function names");
                return;
            }

            var pluginFunctions = toolsToRegister.Select(tool => tool.AsKernelFunction()).ToList();
            kernel.Plugins.AddFromFunctions("McpTools", pluginFunctions);
            logger.LogInformation("Registered {Count} MCP tools based on selection", pluginFunctions.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register MCP tools: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Returns the list of available functions that can be used with the kernel.
    /// </summary>
    public async Task<IEnumerable<FunctionInfo>> GetAvailableFunctionsAsync()
    {
        try
        {
            var mcpClient = await mcpClientService.CreateMcpClientAsync();
            if (mcpClient == null)
            {
                return Enumerable.Empty<FunctionInfo>();
            }

            var mcpTools = await mcpClientService.GetMcpTools(mcpClient);

            return mcpTools
                .Select(tool => new FunctionInfo { Name = tool.Name, Description = tool.Description });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get available functions: {Message}", ex.Message);
            return Enumerable.Empty<FunctionInfo>();
        }
    }
}
