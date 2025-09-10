using System.Text.Json;
using System.Text.Json.Serialization;
using ChatClient.Infrastructure.Constants;
using ChatClient.Domain.Models;
using ChatClient.Application.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Infrastructure.Repositories;

public class SavedChatRepository : ISavedChatRepository
{
    private readonly string _directoryPath;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public SavedChatRepository(IConfiguration configuration, ILogger<SavedChatRepository> logger)
    {
        var path = configuration["SavedChats:DirectoryPath"] ?? FilePathConstants.DefaultSavedChatsDirectory;
        _directoryPath = Path.GetFullPath(path);
        _logger = logger;
        Directory.CreateDirectory(_directoryPath);
    }

    public async Task<List<SavedChat>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Directory.Exists(_directoryPath))
                return [];
            var files = Directory.GetFiles(_directoryPath, "*.json");
            var chats = new List<SavedChat>(files.Length);
            foreach (var file in files)
            {
                try
                {
                    var repo = new JsonFileRepository<SavedChat>(file, _logger, _options);
                    var chat = await repo.ReadAsync(cancellationToken);
                    if (chat != null)
                        chats.Add(chat);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reading saved chat file {File}", file);
                }
            }
            return chats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading saved chats");
            return [];
        }
    }

    public async Task<SavedChat?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Chat id must be provided", nameof(id));
        var file = Path.Combine(_directoryPath, $"{id}.json");
        var repo = new JsonFileRepository<SavedChat>(file, _logger, _options);
        return await repo.ReadAsync(cancellationToken);
    }

    public async Task SaveAsync(SavedChat savedChat, CancellationToken cancellationToken = default)
    {
        if (savedChat is null)
            throw new ArgumentNullException(nameof(savedChat));
        var file = Path.Combine(_directoryPath, $"{savedChat.Id}.json");
        var repo = new JsonFileRepository<SavedChat>(file, _logger, _options);
        await repo.WriteAsync(savedChat, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Chat id must be provided", nameof(id));
        var file = Path.Combine(_directoryPath, $"{id}.json");
        await Task.Run(() =>
        {
            if (File.Exists(file))
                File.Delete(file);
        }, cancellationToken);
    }
}
