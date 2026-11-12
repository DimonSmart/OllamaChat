using ChatClient.Api.Services;

namespace ChatClient.Api.Services.BuiltIn;

public sealed class UserProfileEditorService(IMcpClientService mcpClientService)
{
    public string StorageFilePath => UserProfileStore.Default.FilePath;

    public Task<Dictionary<string, string>> GetAsync(CancellationToken cancellationToken = default) =>
        UserProfileStore.Default.GetAllAsync(cancellationToken);

    public async Task SaveAsync(
        IEnumerable<KeyValuePair<string, string>> values,
        CancellationToken cancellationToken = default)
    {
        await UserProfileStore.Default.SaveAsync(values, cancellationToken);
        await mcpClientService.InvalidateAsync(cancellationToken);
    }
}
