using ChatClient.Api.Client.Services;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using System.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;

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

    public async Task<IReadOnlyCollection<string>> GetFunctionsToRegisterAsync(
        FunctionSettings functionSettings,
        string? userQuery,
        CancellationToken cancellationToken = default)
    {
        if (functionSettings.AutoSelectCount > 0 && !string.IsNullOrWhiteSpace(userQuery))
        {
            return await _indexService.SelectRelevantFunctionsAsync(userQuery, functionSettings.AutoSelectCount, cancellationToken);
        }

        if (functionSettings.SelectedFunctions.Any())
        {
            return functionSettings.SelectedFunctions;
        }

        return [];
    }

    public async Task<Kernel> CreateKernelAsync(
        string modelName,
        IEnumerable<string>? functionsToRegister,
        string agentName,
        CancellationToken cancellationToken = default,
        Guid? serverId = null)
    {
        var settings = await userSettingsService.GetSettingsAsync();
        var targetServer = serverId.HasValue && serverId.Value != Guid.Empty
            ? settings.Llms.FirstOrDefault(s => s.Id == serverId.Value)
            : settings.Llms.FirstOrDefault(s => s.Id == settings.DefaultLlmId) ?? settings.Llms.FirstOrDefault();
        
        var timeoutSeconds = targetServer?.HttpTimeoutSeconds ?? 600;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        
        var httpClient = CreateConfiguredHttpClient(settings, serverId, timeout);
        if (!string.IsNullOrEmpty(agentName))
            httpClient.DefaultRequestHeaders.Add("X-Agent-Name", agentName);

        IKernelBuilder builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(httpClient);
        builder.Services.AddLogging(c => c.AddConsole().SetMinimumLevel(LogLevel.Information));
        builder.Services.AddSingleton<IChatCompletionService>(_ =>
            new AppForceLastUserChatCompletionService(
                new OllamaChatCompletionService(modelName, httpClient: httpClient),
                serviceProvider.GetRequiredService<AppForceLastUserReducer>()));

        var kernel = builder.Build();

        if (functionsToRegister != null && functionsToRegister.Any() && _mcpClientService != null)
        {
            await RegisterMcpToolsAsync(kernel, functionsToRegister, cancellationToken);
        }

        return kernel;
    }

    private HttpClient CreateConfiguredHttpClient(UserSettings settings, Guid? serverId, TimeSpan timeout)
    {
        LlmServerConfig? server = null;
        if (serverId.HasValue && serverId.Value != Guid.Empty)
            server = settings.Llms.FirstOrDefault(s => s.Id == serverId.Value);
        server ??= settings.Llms.FirstOrDefault(s => s.Id == settings.DefaultLlmId) ?? settings.Llms.FirstOrDefault();

        var handler = new HttpClientHandler();
        if (server?.IgnoreSslErrors == true)
            handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;

        var loggingHandler = new HttpLoggingHandler(serviceProvider.GetRequiredService<ILogger<HttpLoggingHandler>>())
        {
            InnerHandler = handler
        };

        var baseUrl = string.IsNullOrWhiteSpace(server?.BaseUrl) ? LlmServerConfig.DefaultOllamaUrl : server.BaseUrl.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = LlmServerConfig.DefaultOllamaUrl;
        }
        var httpClient = new HttpClient(loggingHandler)
        {
            Timeout = TimeSpan.FromSeconds(server?.HttpTimeoutSeconds ?? (int)timeout.TotalSeconds),
            BaseAddress = new Uri(baseUrl)
        };

        var password = server?.Password;
        if (!string.IsNullOrWhiteSpace(password))
        {
            var authValue = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($":{password}"));
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
        }

        return httpClient;
    }

    private async Task RegisterMcpToolsAsync(
        Kernel kernel,
        IEnumerable<string> functionNames,
        CancellationToken cancellationToken)
    {
        if (_mcpClientService == null)
        {
            logger.LogWarning("MCP client service not available for registering tools");
            return;
        }

        try
        {
            var mcpClients = await _mcpClientService.GetMcpClientsAsync(cancellationToken);
            if (mcpClients.Count == 0)
            {
                logger.LogWarning("MCP client could not be created");
                return;
            }

            foreach (var mcpClient in mcpClients)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mcpTools = await _mcpClientService.GetMcpTools(mcpClient, cancellationToken);
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
