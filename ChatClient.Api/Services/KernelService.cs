using ChatClient.Shared.Constants;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

using Microsoft.SemanticKernel;

namespace ChatClient.Api.Services;

public class KernelService(
    IUserSettingsService userSettingsService,
    McpFunctionIndexService indexService,
    ILogger<KernelService> logger,
    IServiceProvider serviceProvider)
{
    private readonly McpFunctionIndexService _indexService = indexService;
    private IMcpClientService? _mcpClientService;

    public void SetMcpClientService(IMcpClientService mcpClientService)
    {
        _mcpClientService = mcpClientService;
    }

    public async Task<Kernel> CreateKernelAsync(ChatConfiguration chatConfiguration, string? userQuery = null, int? autoSelectCount = null)
    {
        var settings = await userSettingsService.GetSettingsAsync();
        var kernel = await CreateBasicKernelAsync(chatConfiguration.ModelName, TimeSpan.FromSeconds(settings.HttpTimeoutSeconds));

        // Register selected MCP tools as kernel functions
        IEnumerable<string>? functionsToRegister = null;
        if (!string.IsNullOrWhiteSpace(userQuery) && autoSelectCount.HasValue && autoSelectCount.Value > 0)
        {
            functionsToRegister = await _indexService.SelectRelevantFunctionsAsync(userQuery, autoSelectCount.Value);
        }
        else if (chatConfiguration.Functions != null && chatConfiguration.Functions.Any())
        {
            functionsToRegister = chatConfiguration.Functions;
        }

        if (functionsToRegister != null && functionsToRegister.Any() && _mcpClientService != null)
        {
            await RegisterMcpToolsAsync(kernel, functionsToRegister);
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

    private HttpClient CreateConfiguredHttpClient(UserSettings settings, TimeSpan timeout)
    {
        var handler = new HttpClientHandler();

        if (settings.IgnoreSslErrors)
        {
            handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
        }

        // Create logging handler and chain it with the base handler
        var loggingHandler = new HttpLoggingHandler(serviceProvider.GetRequiredService<ILogger<HttpLoggingHandler>>())
        {
            InnerHandler = handler
        };

        var httpClient = new HttpClient(loggingHandler)
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

                var toolsToRegister = mcpTools
                    .Where(t => functionNames.Contains($"{mcpClient.ServerInfo.Name}:{t.Name}"))
                    .ToList();
                if (toolsToRegister.Count == 0)
                {
                    logger.LogWarning($"No MCP tools matched the requested function names. In mcp server: {mcpClient.ServerInfo.Name}");
                    continue;
                }
                var pluginFunctions = toolsToRegister.Select(tool => tool.AsKernelFunction()).ToList();
                var pluginName = mcpClient.ServerInfo.Name ?? "McpServer";
                kernel.Plugins.AddFromFunctions(pluginName, pluginFunctions);
                logger.LogInformation("Registered {Count} MCP tools for server {Server}", pluginFunctions.Count, pluginName);
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
                        Name = $"{mcpClient.ServerInfo.Name}:{tool.Name}",
                        ServerName = mcpClient.ServerInfo.Name ?? string.Empty,
                        DisplayName = tool.Name,
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
