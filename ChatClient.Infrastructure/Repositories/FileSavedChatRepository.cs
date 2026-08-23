using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ChatClient.Infrastructure.Repositories;

public sealed class FileSavedChatRepository(ILogger<FileSavedChatRepository> logger) : ISavedChatRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public async Task SaveAsync(string storageRoot, SavedChatDocument chat, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        Directory.CreateDirectory(storageRoot);
        var target = GetPath(storageRoot, chat.Id);
        var temporary = target + ".tmp";
        var json = JsonSerializer.Serialize(chat, JsonOptions);
        await File.WriteAllTextAsync(temporary, json, cancellationToken);
        File.Move(temporary, target, true);
    }

    public async Task<SavedChatDocument?> GetAsync(string storageRoot, Guid id, CancellationToken cancellationToken = default)
    {
        var path = GetPath(storageRoot, id);
        if (!File.Exists(path))
            return null;
        var document = JsonSerializer.Deserialize<SavedChatDocument>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
        if (document is null)
            throw new InvalidDataException("Saved chat file is invalid.");
        if (document.FormatVersion > SavedChatDocument.CurrentFormatVersion)
            throw new InvalidDataException("The saved chat format is newer than this OllamaChat version.");
        if (document.FormatVersion != SavedChatDocument.CurrentFormatVersion)
            throw new InvalidDataException("Saved chat file is invalid.");
        return document;
    }

    public async Task<IReadOnlyList<SavedChatSummary>> GetAllAsync(string storageRoot, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(storageRoot))
            return [];
        var items = new List<SavedChatSummary>();
        foreach (var path in Directory.EnumerateFiles(storageRoot, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = json.RootElement;
                if (!root.TryGetProperty("id", out var idValue) || !idValue.TryGetGuid(out var id) ||
                    !root.TryGetProperty("title", out var titleValue) || !root.TryGetProperty("updatedAtUtc", out var updatedValue))
                    continue;
                var created = root.TryGetProperty("createdAtUtc", out var createdValue) ? createdValue.GetDateTime() : updatedValue.GetDateTime();
                SavedChatRuntimeReference? reference = null;
                if (root.TryGetProperty("launch", out var launch) && launch.TryGetProperty("runtimeReference", out var runtime))
                    reference = runtime.Deserialize<SavedChatRuntimeReference>(JsonOptions);
                items.Add(new SavedChatSummary(id, titleValue.GetString() ?? "New chat", updatedValue.GetDateTime(), created, reference));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            { logger.LogWarning(ex, "Skipping unreadable saved chat file {Path}.", path); }
        }
        return items.OrderByDescending(static item => item.UpdatedAtUtc).ToList();
    }

    private static string GetPath(string root, Guid id) => Path.Combine(Path.GetFullPath(root), $"{id:N}.json");
}
