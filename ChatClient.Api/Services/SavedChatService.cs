using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class SavedChatService(IUserSettingsService settingsService, ISavedChatRepository repository) : ISavedChatService
{
    public async Task<IReadOnlyList<SavedChatSummary>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetSettingsAsync(cancellationToken);
        return await repository.GetAllAsync(settings.SavedChats.StorageRoot, cancellationToken);
    }

    public async Task<SavedChatDocument?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetSettingsAsync(cancellationToken);
        return await repository.GetAsync(settings.SavedChats.StorageRoot, id, cancellationToken);
    }

    public Task<SavedChatDocument?> GetAsync(string storageRoot, Guid id, CancellationToken cancellationToken = default) =>
        repository.GetAsync(storageRoot, id, cancellationToken);

    public async Task SaveAsync(SavedChatDocument chat, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetSettingsAsync(cancellationToken);
        if (settings.SavedChats.AutoSaveEnabled)
        {
            chat.StorageRoot ??= Path.GetFullPath(settings.SavedChats.StorageRoot);
            await repository.SaveAsync(chat.StorageRoot, chat, cancellationToken);
        }
    }

    public async Task SaveCheckpointAsync(SavedChatDocument chat, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetSettingsAsync(cancellationToken);
        if (settings.SavedChats.AutoSaveEnabled)
        {
            chat.StorageRoot ??= Path.GetFullPath(settings.SavedChats.StorageRoot);
            await repository.SaveCheckpointAsync(chat.StorageRoot, chat, cancellationToken);
        }
    }

    public async Task RenameAsync(Guid id, string title, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetSettingsAsync(cancellationToken);
        await repository.UpdateAsync(settings.SavedChats.StorageRoot, id, chat =>
        {
            chat.Title = string.IsNullOrWhiteSpace(title) ? "New chat" : title.Trim();
            chat.IsTitleManual = true;
            chat.UpdatedAtUtc = DateTime.UtcNow;
            return chat;
        }, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetSettingsAsync(cancellationToken);
        await repository.DeleteAsync(settings.SavedChats.StorageRoot, id, cancellationToken);
    }

    public async Task<bool> IsAutoSaveEnabledAsync(CancellationToken cancellationToken = default) =>
        (await settingsService.GetSettingsAsync(cancellationToken)).SavedChats.AutoSaveEnabled;
}
