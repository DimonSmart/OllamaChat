using ChatClient.Domain.Models;

namespace ChatClient.Api.Client.Models;

public sealed class UiModelSelectionContext(
    ServerModelSelection value,
    Func<ServerModelSelection, Task> setValueAsync)
{
    public ServerModelSelection Value { get; private set; } = value;

    public Task SetValueAsync(ServerModelSelection value) => setValueAsync(value);

    internal void UpdateValue(ServerModelSelection value) => Value = value;
}
