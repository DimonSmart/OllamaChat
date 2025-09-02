using Microsoft.AspNetCore.Components.Server.Circuits;
using System.Threading;

namespace ChatClient.Api.Services;

/// <summary>
/// Stops the application when the last circuit ends to avoid shutdowns during transient disconnects.
/// </summary>
public sealed class AutoShutdownCircuitHandler(IHostApplicationLifetime lifetime) : CircuitHandler
{
    private int circuits;

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref circuits);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (Interlocked.Decrement(ref circuits) == 0)
        {
            lifetime.StopApplication();
        }

        return Task.CompletedTask;
    }
}
