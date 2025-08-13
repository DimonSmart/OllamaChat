#pragma warning disable SKEXP0110

namespace ChatClient.Api.Client.Services;

internal sealed class TrackingFiltersScope : IDisposable
{
    private readonly Action _onDispose;

    public TrackingFiltersScope(Action onDispose)
    {
        _onDispose = onDispose;
    }

    public void Dispose()
    {
        _onDispose();
    }
}

#pragma warning restore SKEXP0110
