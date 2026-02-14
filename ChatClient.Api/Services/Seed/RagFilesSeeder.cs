using ChatClient.Infrastructure.Constants;
using ChatClient.Infrastructure.Helpers;

namespace ChatClient.Api.Services.Seed;

public class RagFilesSeeder(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<RagFilesSeeder> logger)
{
    private readonly IConfiguration _configuration = configuration;
    private readonly IHostEnvironment _environment = environment;
    private readonly ILogger<RagFilesSeeder> _logger = logger;

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var sourceBasePath = StoragePathResolver.ResolveSeedPath(
            _configuration,
            _environment.ContentRootPath,
            _configuration["RagFiles:SeedBasePath"],
            "agents");

        if (!Directory.Exists(sourceBasePath))
        {
            return;
        }

        var targetBasePath = StoragePathResolver.ResolveUserPath(
            _configuration,
            _configuration["RagFiles:BasePath"],
            FilePathConstants.DefaultRagFilesDirectory);

        Directory.CreateDirectory(targetBasePath);

        var copiedFiles = 0;
        foreach (var sourceAgentPath in Directory.EnumerateDirectories(sourceBasePath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceFilesPath = Path.Combine(sourceAgentPath, "files");
            if (!Directory.Exists(sourceFilesPath))
            {
                continue;
            }

            var agentFolderName = Path.GetFileName(sourceAgentPath);
            if (string.IsNullOrWhiteSpace(agentFolderName))
            {
                continue;
            }

            var targetFilesPath = Path.Combine(targetBasePath, agentFolderName, "files");
            Directory.CreateDirectory(targetFilesPath);

            foreach (var sourceFile in Directory.EnumerateFiles(sourceFilesPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = Path.GetFileName(sourceFile);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                var targetFile = Path.Combine(targetFilesPath, fileName);
                if (File.Exists(targetFile))
                {
                    continue;
                }

                await using var source = File.OpenRead(sourceFile);
                await using var target = File.Create(targetFile);
                await source.CopyToAsync(target, cancellationToken);
                copiedFiles++;
            }
        }

        if (copiedFiles > 0)
        {
            _logger.LogInformation("Seeded {Count} RAG source files into {TargetPath}", copiedFiles, targetBasePath);
        }
    }
}
