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

    public async Task SaveAsync(SavedChatDocument chat, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetSettingsAsync(cancellationToken);
        if (settings.SavedChats.AutoSaveEnabled)
            await repository.SaveAsync(settings.SavedChats.StorageRoot, chat, cancellationToken);
    }

    public async Task RenameAsync(Guid id, string title, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetSettingsAsync(cancellationToken);
        var chat = await repository.GetAsync(settings.SavedChats.StorageRoot, id, cancellationToken)
            ?? throw new InvalidOperationException("Saved chat does not exist.");
        chat.Title = string.IsNullOrWhiteSpace(title) ? "New chat" : title.Trim();
        chat.IsTitleManual = true;
        chat.UpdatedAtUtc = DateTime.UtcNow;
        await repository.SaveAsync(settings.SavedChats.StorageRoot, chat, cancellationToken);
    }

    public async Task<bool> IsAutoSaveEnabledAsync(CancellationToken cancellationToken = default) =>
        (await settingsService.GetSettingsAsync(cancellationToken)).SavedChats.AutoSaveEnabled;
}
