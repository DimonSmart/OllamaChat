using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Configuration;

namespace ChatClient.Infrastructure.Repositories;

public class RagFileRepository(IConfiguration configuration) : IRagFileRepository
{
    private readonly string _basePath = configuration["RagFiles:BasePath"] ?? Path.Combine("Data", "agents");

    public async Task<IReadOnlyCollection<RagFile>> GetFilesAsync(Guid agentId)
    {
        var agentFolder = GetAgentFolder(agentId);
        var filesDir = Path.Combine(agentFolder, "files");
        if (!Directory.Exists(filesDir))
            return [];

        List<RagFile> result = [];
        foreach (var file in Directory.GetFiles(filesDir))
        {
            var fileName = Path.GetFileName(file);
            var content = await File.ReadAllTextAsync(file);
            var size = new FileInfo(file).Length;
            result.Add(new RagFile
            {
                FileName = fileName,
                Content = content,
                Size = size,
                HasIndex = false
            });
        }
        return result;
    }

    public async Task<RagFile?> GetFileAsync(Guid agentId, string fileName)
    {
        var agentFolder = GetAgentFolder(agentId);
        var filePath = Path.Combine(agentFolder, "files", fileName);
        if (!File.Exists(filePath))
            return null;

        var content = await File.ReadAllTextAsync(filePath);
        var size = new FileInfo(filePath).Length;
        return new RagFile
        {
            FileName = fileName,
            Content = content,
            Size = size,
            HasIndex = false
        };
    }

    public async Task AddOrUpdateFileAsync(Guid agentId, RagFile file)
    {
        var agentFolder = GetAgentFolder(agentId);
        Directory.CreateDirectory(Path.Combine(agentFolder, "files"));

        var filePath = Path.Combine(agentFolder, "files", file.FileName);
        await File.WriteAllTextAsync(filePath, file.Content);
    }

    public Task DeleteFileAsync(Guid agentId, string fileName)
    {
        var agentFolder = GetAgentFolder(agentId);
        var filePath = Path.Combine(agentFolder, "files", fileName);
        if (File.Exists(filePath))
            File.Delete(filePath);
        return Task.CompletedTask;
    }

    private string GetAgentFolder(Guid agentId) => Path.Combine(_basePath, agentId.ToString());
}
