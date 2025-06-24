using ChatClient.Shared.Constants;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using Microsoft.SemanticKernel;

namespace ChatClient.Api.Services;

public class KernelService(
    IUserSettingsService userSettingsService,
    ILogger<KernelService> logger)
{
    private McpClientService? _mcpClientService;

    public void SetMcpClientService(McpClientService mcpClientService)
    {
        _mcpClientService = mcpClientService;
    }

    public async Task<Kernel> CreateKernelAsync(ChatConfiguration chatConfiguration)
    {
        var settings = await userSettingsService.GetSettingsAsync();
        var kernel = await CreateBasicKernelAsync(chatConfiguration.ModelName, TimeSpan.FromSeconds(settings.HttpTimeoutSeconds));

        // Register selected MCP tools as kernel functions
        if (chatConfiguration.Functions != null && chatConfiguration.Functions.Any() && _mcpClientService != null)
        {
            await RegisterMcpToolsAsync(kernel, chatConfiguration.Functions);
        }

        return kernel;
    }

    public async Task<Kernel> CreateBasicKernelAsync(string modelId, TimeSpan timeout)
    {
        var settings = await userSettingsService.GetSettingsAsync();
        var baseUrl = !string.IsNullOrWhiteSpace(settings.OllamaServerUrl) ? settings.OllamaServerUrl : OllamaDefaults.ServerUrl;

        IKernelBuilder builder = Kernel.CreateBuilder();
        var httpClient = CreateConfiguredHttpClient(settings, timeout);
        httpClient.BaseAddress = new Uri(baseUrl);
        builder.AddOllamaChatCompletion(modelId: modelId, httpClient: httpClient);
        builder.Services.AddSingleton(httpClient);
        builder.Services.AddLogging(c => c.AddConsole().SetMinimumLevel(LogLevel.Information));

        return builder.Build();
    }

    private static HttpClient CreateConfiguredHttpClient(UserSettings settings, TimeSpan timeout)
    {
        var handler = new HttpClientHandler();

        if (settings.IgnoreSslErrors)
        {
            handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
        }

        var httpClient = new HttpClient(handler)
        {
            Timeout = timeout
        };

        if (!string.IsNullOrWhiteSpace(settings.OllamaBasicAuthPassword))
        {
            var authValue = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($":{settings.OllamaBasicAuthPassword}"));
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
        }

        return httpClient;
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
                    logger.LogWarning($"No MCP tools available to register for server: {mcpClient.ServerInfo.Name} ");
                    continue;
                }

                var toolsToRegister = mcpTools.Where(t => functionNames.Contains(t.Name)).ToList();
                if (toolsToRegister.Count == 0)
                {
                    logger.LogWarning($"No MCP tools matched the requested function names. In mcp server: {mcpClient.ServerInfo.Name}");
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
            if (mcpClients.Count == 0)
                return [];
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
