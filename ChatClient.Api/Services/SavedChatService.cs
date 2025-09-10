using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using System.Linq;

namespace ChatClient.Api.Services;

public class SavedChatService(ISavedChatRepository repository) : ISavedChatService
{
    private readonly ISavedChatRepository _repository = repository;

    public Task<IReadOnlyCollection<SavedChat>> GetAllAsync(CancellationToken cancellationToken = default) =>
        _repository.GetAllAsync(cancellationToken);

    public async Task<IReadOnlyCollection<SavedChat>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var chats = await _repository.GetAllAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(query))
            return chats;
        return chats.Where(c =>
                c.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                c.Participants.Any(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public Task<SavedChat?> GetByIdAsync(Guid chatId, CancellationToken cancellationToken = default) =>
        _repository.GetByIdAsync(chatId, cancellationToken);

    public Task SaveAsync(SavedChat savedChat, CancellationToken cancellationToken = default) =>
        _repository.SaveAsync(savedChat, cancellationToken);

    public Task DeleteAsync(Guid chatId, CancellationToken cancellationToken = default) =>
        _repository.DeleteAsync(chatId, cancellationToken);
}
