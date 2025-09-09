using ChatClient.Shared.Constants;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatClient.Api.Services;

public class SavedChatService : ISavedChatService
{
    private readonly string _directoryPath;
    private readonly ILogger<SavedChatService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SavedChatService(IConfiguration configuration, ILogger<SavedChatService> logger)
    {
        _logger = logger;
        var path = configuration["SavedChats:DirectoryPath"] ?? FilePathConstants.DefaultSavedChatsDirectory;
        _directoryPath = Path.GetFullPath(path);
        Directory.CreateDirectory(_directoryPath);
        _logger.LogInformation("Saved chats directory: {DirectoryPath}", _directoryPath);
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
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
                    var json = await File.ReadAllTextAsync(file, cancellationToken);
                    var chat = JsonSerializer.Deserialize<SavedChat>(json, _jsonOptions);
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

    public async Task<List<SavedChat>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var chats = await GetAllAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(query))
            return chats;
        return chats.Where(c =>
                c.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                c.Participants.Any(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public async Task<SavedChat?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Chat id must be provided", nameof(id));
        var file = Path.Combine(_directoryPath, $"{id}.json");
        if (!File.Exists(file))
            return null;
        try
        {
            var json = await File.ReadAllTextAsync(file, cancellationToken);
            return JsonSerializer.Deserialize<SavedChat>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading saved chat {ChatId}", id);
            return null;
        }
    }

    public async Task SaveAsync(SavedChat savedChat, CancellationToken cancellationToken = default)
    {
        if (savedChat is null)
            throw new ArgumentNullException(nameof(savedChat));
        var file = Path.Combine(_directoryPath, $"{savedChat.Id}.json");
        await WriteAsync(file, savedChat, cancellationToken);
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

    private async Task WriteAsync(string file, SavedChat chat, CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(chat, _jsonOptions);
            await File.WriteAllTextAsync(file, json, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing saved chat {ChatId}", chat.Id);
        }
    }
}
