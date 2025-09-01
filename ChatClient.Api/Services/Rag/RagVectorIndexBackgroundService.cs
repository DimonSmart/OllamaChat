using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

namespace ChatClient.Api.Services.Rag;

public sealed class RagVectorIndexBackgroundService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<RagVectorIndexBackgroundService> logger) : BackgroundService, IRagVectorIndexBackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<RagVectorIndexBackgroundService> _logger = logger;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Lock _sync = new();
    private bool _rescanRequested;
    private bool _running;
    private RagVectorIndexStatus? _currentStatus;

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

    public RagVectorIndexStatus? GetCurrentStatus() => _currentStatus;

    private async Task RebuildMissingIndexesAsync(CancellationToken token)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var agentService = scope.ServiceProvider.GetRequiredService<IAgentDescriptionService>();
            var fileService = scope.ServiceProvider.GetRequiredService<IRagFileService>();
            var indexService = scope.ServiceProvider.GetRequiredService<IRagVectorIndexService>();
            var settingsService = scope.ServiceProvider.GetRequiredService<IUserSettingsService>();
            var settings = await settingsService.GetSettingsAsync();
            var embedServer = settings.Embedding.Model.ServerId;

            var basePath = _configuration["RagFiles:BasePath"] ?? Path.Combine("Data", "agents");
            List<(Guid agentId, string source, string index, string fileName)> pending = [];
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
                    pending.Add((agent.Id, sourcePath, indexPath, file.FileName));
                }
            }

            _logger.LogInformation("Vector index rebuild queue has {Count} files", pending.Count);

            for (var i = 0; i < pending.Count; i++)
            {
                var item = pending[i];
                _logger.LogInformation("Indexing {File} ({Current}/{Total})", item.fileName, i + 1, pending.Count);
                var progress = new Progress<RagVectorIndexStatus>(s => _currentStatus = s);
                _currentStatus = new(item.agentId, item.fileName, 0, 0);
                await indexService.BuildIndexAsync(item.agentId, item.source, item.index, progress, token, embedServer);
                _currentStatus = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build vector indexes");
        }
    }
}
