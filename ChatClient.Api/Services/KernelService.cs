using ChatClient.Shared.Models;

using Microsoft.SemanticKernel;

using OllamaSharp;

namespace ChatClient.Api.Services;

public class KernelService(
    IConfiguration configuration,
    OllamaService ollamaService,
    ILogger<KernelService> logger)
{
    private McpClientService? _mcpClientService;

    public void SetMcpClientService(McpClientService mcpClientService)
    {
        _mcpClientService = mcpClientService;
    }

    public async Task<Kernel> CreateKernelAsync(string modelId, IEnumerable<string>? functionNames = null)
    {
        var kernel = CreateBasicKernel(modelId);

        // Register selected MCP tools as kernel functions
        if (functionNames != null && functionNames.Any() && _mcpClientService != null)
        {
            await RegisterMcpToolsAsync(kernel, functionNames);
        }

        return kernel;
    }
    public Kernel CreateBasicKernel(string modelId)
    {
        var ollamaClient = ollamaService.GetClient();

        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.AddOllamaTextGeneration(modelId, ollamaClient);

        builder.Services.AddLogging(c => c.AddConsole().SetMinimumLevel(LogLevel.Information));
        builder.Services.AddSingleton(new PromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
        });

        return builder.Build();
    }
    private async Task RegisterMcpToolsAsync(Kernel kernel, IEnumerable<string> functionNames)
    {
        if (_mcpClientService == null)
        {
            logger.LogWarning("MCP client service not available for registering tools");
            return;
        }

        try
        {
            var mcpClients = await _mcpClientService.GetMcpClientsAsync();
            if (mcpClients.Count == 0)
            {
                logger.LogWarning("MCP client could not be created");
                return;
            }

            foreach (var mcpClient in mcpClients)
            {
                var mcpTools = await _mcpClientService.GetMcpTools(mcpClient);
                if (mcpTools.Count == 0)
                {
                    logger.LogWarning("No MCP tools available to register");
                    continue;
                }

                var toolsToRegister = mcpTools.Where(t => functionNames.Contains(t.Name)).ToList();
                if (toolsToRegister.Count == 0)
                {
                    logger.LogWarning("No MCP tools matched the requested function names");
                    continue;
                }
                var pluginFunctions = toolsToRegister.Select(tool => tool.AsKernelFunction()).ToList();
                kernel.Plugins.AddFromFunctions("McpTools", pluginFunctions);
                logger.LogInformation("Registered {Count} MCP tools based on selection", pluginFunctions.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register MCP tools: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Returns the list of available functions that can be used with the kernel.
    /// </summary>
    public async Task<IReadOnlyCollection<FunctionInfo>> GetAvailableFunctionsAsync()
    {
        var functions = new List<FunctionInfo>();

        if (_mcpClientService == null)
        {
            logger.LogWarning("MCP client service not available for getting functions");
            return functions;
        }

        try
        {
            var mcpClients = await _mcpClientService.GetMcpClientsAsync();
            if (mcpClients.Count == 0) return [];
            foreach (var mcpClient in mcpClients)
            {
                var mcpTools = await _mcpClientService.GetMcpTools(mcpClient);
                var toolFuncs = mcpTools.Select(tool =>
                    new FunctionInfo
                    {
                        Name = tool.Name,
                        Description = tool.Description
                    });
                functions.AddRange(toolFuncs);
            }

            return functions;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get available functions: {Message}", ex.Message);
            return functions;
        }
    }
}
