using ChatClient.Application.Repositories;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace ChatClient.Infrastructure.Repositories;

public sealed class FileSavedChatRepository(ILogger<FileSavedChatRepository> logger) : ISavedChatRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates = new(StringComparer.OrdinalIgnoreCase);

    public async Task SaveAsync(string storageRoot, SavedChatDocument chat, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        var target = GetPath(storageRoot, chat.Id);
        await WithFileGateAsync(target, async () =>
        {
            Directory.CreateDirectory(storageRoot);
            var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var json = JsonSerializer.Serialize(chat, JsonOptions);
                await File.WriteAllTextAsync(temporary, json, cancellationToken);
                File.Move(temporary, target, true);
            }
            finally
            {
                try
                { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
        }, cancellationToken);
    }

    public async Task<SavedChatDocument?> GetAsync(string storageRoot, Guid id, CancellationToken cancellationToken = default)
    {
        var path = GetPath(storageRoot, id);
        SavedChatDocument? document = null;
        await WithFileGateAsync(path, async () =>
        {
            if (File.Exists(path))
                document = JsonSerializer.Deserialize<SavedChatDocument>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
        }, cancellationToken);
        if (document is null && !File.Exists(path))
            return null;
        if (document is null)
            throw new InvalidDataException("Saved chat file is invalid.");
        if (document.FormatVersion > SavedChatDocument.CurrentFormatVersion)
            throw new InvalidDataException("The saved chat format is newer than this OllamaChat version.");
        if (document.FormatVersion != SavedChatDocument.CurrentFormatVersion)
            throw new InvalidDataException("Saved chat file is invalid.");
        document.StorageRoot = Path.GetFullPath(storageRoot);
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

    private static async Task WithFileGateAsync(string path, Func<Task> action, CancellationToken cancellationToken)
    {
        var gate = FileGates.GetOrAdd(Path.GetFullPath(path), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        { await action(); }
        finally { gate.Release(); }
    }
}
