using ChatClient.Shared.Services;

namespace ChatClient.Api.Services;

public sealed class RagVectorIndexBackgroundService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<RagVectorIndexBackgroundService> logger) : BackgroundService, IRagVectorIndexBackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<RagVectorIndexBackgroundService> _logger = logger;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _sync = new();
    private bool _rescanRequested;
    private bool _running;

    public void RequestRebuild()
    {
        lock (_sync)
        {
            if (_running)
            {
                _rescanRequested = true;
                return;
            }
            _running = true;
            _signal.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RequestRebuild();
        while (!stoppingToken.IsCancellationRequested)
        {
            await _signal.WaitAsync(stoppingToken);
            await RebuildMissingIndexesAsync(stoppingToken);
            lock (_sync)
            {
                if (_rescanRequested && !stoppingToken.IsCancellationRequested)
                {
                    _rescanRequested = false;
                    _signal.Release();
                }
                else
                {
                    _running = false;
                }
            }
        }
    }

    private async Task RebuildMissingIndexesAsync(CancellationToken token)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var agentService = scope.ServiceProvider.GetRequiredService<IAgentDescriptionService>();
            var fileService = scope.ServiceProvider.GetRequiredService<IRagFileService>();
            var indexService = scope.ServiceProvider.GetRequiredService<IRagVectorIndexService>();

            var basePath = _configuration["RagFiles:BasePath"] ?? Path.Combine("Data", "agents");
            var agents = await agentService.GetAllAsync();
            foreach (var agent in agents)
            {
                var agentFolder = Path.Combine(basePath, agent.Id.ToString());
                var files = await fileService.GetFilesAsync(agent.Id);
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    if (file.HasIndex)
                        continue;

                    var sourcePath = Path.Combine(agentFolder, "files", file.FileName);
                    var indexPath = Path.Combine(agentFolder, "index", Path.ChangeExtension(file.FileName, ".idx"));
                    await indexService.BuildIndexAsync(sourcePath, indexPath, token);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build vector indexes");
        }
    }
}
