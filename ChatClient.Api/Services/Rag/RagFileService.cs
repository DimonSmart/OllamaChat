using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

namespace ChatClient.Api.Services.Rag;

public class RagFileService(IRagVectorIndexBackgroundService indexBackgroundService, IConfiguration configuration) : IRagFileService
{
    private readonly IRagVectorIndexBackgroundService _indexBackgroundService = indexBackgroundService;
    private readonly string _basePath = configuration["RagFiles:BasePath"] ?? Path.Combine("Data", "agents");

    public async Task<List<RagFile>> GetFilesAsync(Guid id)
    {
        var agentFolder = GetAgentFolder(id);
        var filesDir = Path.Combine(agentFolder, "files");
        if (!Directory.Exists(filesDir))
            return [];

        var indexDir = Path.Combine(agentFolder, "index");
        var result = new List<RagFile>();
        foreach (var file in Directory.GetFiles(filesDir))
        {
            var fileName = Path.GetFileName(file);
            var content = await File.ReadAllTextAsync(file);
            var size = new FileInfo(file).Length;
            var hasIndex = File.Exists(Path.Combine(indexDir, Path.ChangeExtension(fileName, ".idx")));
            result.Add(new RagFile
            {
                FileName = fileName,
                Content = content,
                Size = size,
                HasIndex = hasIndex
            });
        }
        return result;
    }

    public async Task<RagFile?> GetFileAsync(Guid id, string fileName)
    {
        var agentFolder = GetAgentFolder(id);
        var filePath = Path.Combine(agentFolder, "files", fileName);
        if (!File.Exists(filePath))
            return null;

        var content = await File.ReadAllTextAsync(filePath);
        var size = new FileInfo(filePath).Length;
        var hasIndex = File.Exists(Path.Combine(agentFolder, "index", Path.ChangeExtension(fileName, ".idx")));
        return new RagFile
        {
            FileName = fileName,
            Content = content,
            Size = size,
            HasIndex = hasIndex
        };
    }

    public async Task AddOrUpdateFileAsync(Guid id, RagFile file)
    {
        var agentFolder = GetAgentFolder(id);
        Directory.CreateDirectory(Path.Combine(agentFolder, "files"));
        Directory.CreateDirectory(Path.Combine(agentFolder, "index"));

        var filePath = Path.Combine(agentFolder, "files", file.FileName);
        await File.WriteAllTextAsync(filePath, file.Content);

        var indexPath = Path.Combine(agentFolder, "index", Path.ChangeExtension(file.FileName, ".idx"));
        if (File.Exists(indexPath))
            File.Delete(indexPath);
        _indexBackgroundService.RequestRebuild();
    }

    public Task DeleteFileAsync(Guid id, string fileName)
    {
        var agentFolder = GetAgentFolder(id);
        var indexPath = Path.Combine(agentFolder, "index", Path.ChangeExtension(fileName, ".idx"));
        if (File.Exists(indexPath))
            File.Delete(indexPath);

        var filePath = Path.Combine(agentFolder, "files", fileName);
        if (File.Exists(filePath))
            File.Delete(filePath);
        _indexBackgroundService.RequestRebuild();
        return Task.CompletedTask;
    }

    private string GetAgentFolder(Guid id) => Path.Combine(_basePath, id.ToString());
}

