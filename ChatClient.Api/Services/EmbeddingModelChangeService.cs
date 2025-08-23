using ChatClient.Shared.Services;

namespace ChatClient.Api.Services;

public class EmbeddingModelChangeService(
    IConfiguration configuration,
    IRagVectorIndexBackgroundService indexBackgroundService,
    McpFunctionIndexService mcpFunctionIndexService,
    ILogger<EmbeddingModelChangeService> logger) : IEmbeddingModelChangeService
{
    private readonly IConfiguration _configuration = configuration;
    private readonly IRagVectorIndexBackgroundService _indexBackgroundService = indexBackgroundService;
    private readonly McpFunctionIndexService _mcpFunctionIndexService = mcpFunctionIndexService;
    private readonly ILogger<EmbeddingModelChangeService> _logger = logger;

    public async Task HandleChangeAsync()
    {
        var basePath = _configuration["RagFiles:BasePath"] ?? Path.Combine("Data", "agents");
        if (Directory.Exists(basePath))
        {
            foreach (var file in Directory.GetFiles(basePath, "*.idx", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete index {File}", file);
                }
            }
        }

        _mcpFunctionIndexService.Invalidate();
        await _mcpFunctionIndexService.BuildIndexAsync();
        _indexBackgroundService.RequestRebuild();
    }
}
