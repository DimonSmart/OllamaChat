using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface ILocalMarkdownPluginService
{
    Task<IReadOnlyCollection<LocalMarkdownPlugin>> GetAllAsync();
    Task<LocalMarkdownPlugin> RegisterAsync(string directory);
    Task SetEnabledAsync(Guid id, bool enabled);
    Task RemoveAsync(Guid id);
    LocalMarkdownPluginInspection Inspect(LocalMarkdownPlugin plugin);
}
