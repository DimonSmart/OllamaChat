using System.IO;

using ChatClient.Shared.Models;
using ChatClient.Shared.Services;

namespace ChatClient.Api.Services;

public class RagFileService : IRagFileService
{
    private readonly string _basePath;

    public RagFileService(IConfiguration configuration)
    {
        _basePath = configuration["RagFiles:BasePath"] ?? Path.Combine("Data", "agents");
    }

    public async Task<List<RagFile>> GetFilesAsync(Guid id)
    {
        var filesDir = Path.Combine(GetAgentFolder(id), "files");
        if (!Directory.Exists(filesDir))
            return [];

        var result = new List<RagFile>();
        foreach (var file in Directory.GetFiles(filesDir))
        {
            var content = await File.ReadAllTextAsync(file);
            result.Add(new RagFile { FileName = Path.GetFileName(file), Content = content });
        }
        return result;
    }

    public async Task<RagFile?> GetFileAsync(Guid id, string fileName)
    {
        var filePath = Path.Combine(GetAgentFolder(id), "files", fileName);
        if (!File.Exists(filePath))
            return null;

        var content = await File.ReadAllTextAsync(filePath);
        return new RagFile { FileName = fileName, Content = content };
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

        // TODO: trigger index rebuild
    }

    public Task DeleteFileAsync(Guid id, string fileName)
    {
        var agentFolder = GetAgentFolder(id);
        var filePath = Path.Combine(agentFolder, "files", fileName);
        if (File.Exists(filePath))
            File.Delete(filePath);

        var indexPath = Path.Combine(agentFolder, "index", Path.ChangeExtension(fileName, ".idx"));
        if (File.Exists(indexPath))
            File.Delete(indexPath);

        // TODO: trigger index rebuild
        return Task.CompletedTask;
    }

    private string GetAgentFolder(Guid id) => Path.Combine(_basePath, id.ToString());
}

