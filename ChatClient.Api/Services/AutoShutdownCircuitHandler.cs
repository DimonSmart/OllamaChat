using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Logging;

namespace ChatClient.Api.Services;

/// <summary>
/// Stops the application when no active browser connections remain after a short delay to allow quick reconnects.
/// </summary>
public sealed class AutoShutdownCircuitHandler(
    IHostApplicationLifetime lifetime,
    ILogger<AutoShutdownCircuitHandler> logger) : CircuitHandler
{
    private readonly Lock _sync = new();
    private readonly HashSet<string> _connectedCircuitIds = [];
    private int _shutdownSequence;

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var activeConnections = 0;

        using (var guard = _sync.EnterScope())
        {
            _connectedCircuitIds.Add(circuit.Id);
            _shutdownSequence++;
            activeConnections = _connectedCircuitIds.Count;
        }

        logger.LogInformation(
            "Blazor connection established. CircuitId={CircuitId} ActiveConnections={ActiveConnections}",
            circuit.Id,
            activeConnections);

        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var shouldScheduleShutdown = false;
        var shutdownSequence = 0;
        var activeConnections = 0;

        using (var guard = _sync.EnterScope())
        {
            _connectedCircuitIds.Remove(circuit.Id);
            shutdownSequence = ++_shutdownSequence;
            activeConnections = _connectedCircuitIds.Count;
            shouldScheduleShutdown = activeConnections == 0;
        }

        logger.LogInformation(
            "Blazor connection dropped. CircuitId={CircuitId} ActiveConnections={ActiveConnections}",
            circuit.Id,
            activeConnections);

        if (!shouldScheduleShutdown)
        {
            return Task.CompletedTask;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));

            var stopApplication = false;
            using (var guard = _sync.EnterScope())
            {
                stopApplication = _connectedCircuitIds.Count == 0 && _shutdownSequence == shutdownSequence;
            }

            if (!stopApplication)
            {
                return;
            }

            logger.LogInformation("No active Blazor connections remain. Stopping application.");
            lifetime.StopApplication();
        });

        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        using var guard = _sync.EnterScope();
        _connectedCircuitIds.Remove(circuit.Id);
        return Task.CompletedTask;
    }
}
