using Microsoft.AspNetCore.Components.Server.Circuits;
using System.Threading;

namespace ChatClient.Api.Services;

/// <summary>
/// Stops the application when the last browser connection closes.
/// </summary>
public sealed class AutoShutdownCircuitHandler(IHostApplicationLifetime lifetime) : CircuitHandler
{
    private int connections;

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref connections);
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (Interlocked.Decrement(ref connections) == 0)
        {
            lifetime.StopApplication();
        }

        return Task.CompletedTask;
    }
}
