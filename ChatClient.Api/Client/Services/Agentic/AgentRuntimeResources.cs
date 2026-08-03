namespace ChatClient.Api.Client.Services.Agentic;

internal sealed class AgentRuntimeResources(ILogger logger) : IDisposable
{
    private readonly List<IDisposable> ownedResources = [];
    private bool disposed;

    public T Own<T>(T resource)
        where T : IDisposable
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(resource);

        if (ownedResources.Contains(resource, ReferenceEqualityComparer.Instance))
            return resource;

        ownedResources.Add(resource);
        return resource;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        for (var index = ownedResources.Count - 1; index >= 0; index--)
        {
            try
            {
                ownedResources[index].Dispose();
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Runtime chat-client cleanup failed.");
            }
        }

        ownedResources.Clear();
    }
}
