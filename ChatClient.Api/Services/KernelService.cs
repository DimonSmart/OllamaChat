using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace ChatClient.Api.Services;

public class KernelService(
    McpFunctionIndexService indexService,
    IAppToolCatalog appToolCatalog,
    ILogger<KernelService> logger)
{
    private readonly McpFunctionIndexService _indexService = indexService;
    private readonly IAppToolCatalog _appToolCatalog = appToolCatalog;

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

    public async Task<IReadOnlyCollection<FunctionInfo>> GetAvailableFunctionsAsync(CancellationToken cancellationToken = default)
    {
        List<FunctionInfo> functions = [];

        try
        {
            var tools = await _appToolCatalog.ListToolsAsync(cancellationToken);
            if (tools.Count == 0)
                return [];

            foreach (var tool in tools)
            {
                functions.Add(new FunctionInfo
                {
                    Name = tool.QualifiedName,
                    ServerName = tool.ServerName,
                    DisplayName = tool.DisplayName,
                    Description = tool.Description
                });
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
