using ChatClient.Api.Services;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ChatClient.Tests;

public class AutoShutdownCircuitHandlerTests
{
    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopped = true;
    }

    [Fact]
    public async Task ClosesWithoutReconnect_StopsAfterDelay()
    {
        var lifetime = new TestLifetime();
        var handler = new AutoShutdownCircuitHandler(lifetime, NullLogger<AutoShutdownCircuitHandler>.Instance);

        await handler.OnConnectionUpAsync(CreateCircuit("c1"), default);
        await handler.OnConnectionDownAsync(CreateCircuit("c1"), default);
        Assert.False(lifetime.Stopped);

        await WaitUntilAsync(() => lifetime.Stopped, TimeSpan.FromSeconds(6));
        Assert.True(lifetime.Stopped);
    }

    [Fact]
    public async Task ReconnectWithinDelay_DoesNotStop()
    {
        var lifetime = new TestLifetime();
        var handler = new AutoShutdownCircuitHandler(lifetime, NullLogger<AutoShutdownCircuitHandler>.Instance);

        await handler.OnConnectionUpAsync(CreateCircuit("c1"), default);
        await handler.OnConnectionDownAsync(CreateCircuit("c1"), default);

        await Task.Delay(TimeSpan.FromMilliseconds(100));
        await handler.OnConnectionUpAsync(CreateCircuit("c1"), default);
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.False(lifetime.Stopped);
    }

    [Fact]
    public async Task AnotherActiveConnection_PreventsShutdown()
    {
        var lifetime = new TestLifetime();
        var handler = new AutoShutdownCircuitHandler(lifetime, NullLogger<AutoShutdownCircuitHandler>.Instance);

        await handler.OnConnectionUpAsync(CreateCircuit("c1"), default);
        await handler.OnConnectionUpAsync(CreateCircuit("c2"), default);
        await handler.OnConnectionDownAsync(CreateCircuit("c1"), default);
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.False(lifetime.Stopped);
    }

    private static Circuit CreateCircuit(string id)
    {
        var circuitHost = RuntimeHelpers.GetUninitializedObject(CircuitHostType);
        var circuitId = Activator.CreateInstance(
            CircuitIdType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["test-secret", id],
            culture: null)
            ?? throw new InvalidOperationException("Unable to create CircuitId instance.");

        CircuitIdField.SetValue(circuitHost, circuitId);

        var circuit = (Circuit)RuntimeHelpers.GetUninitializedObject(typeof(Circuit));
        CircuitHostField.SetValue(circuit, circuitHost);
        return circuit;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var startedAt = DateTime.UtcNow;
        while (!predicate())
        {
            if (DateTime.UtcNow - startedAt > timeout)
            {
                return;
            }

            await Task.Delay(25);
        }
    }

    private static readonly Type CircuitHostType = typeof(Circuit).Assembly
        .GetType("Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost")
        ?? throw new InvalidOperationException("CircuitHost type was not found.");

    private static readonly Type CircuitIdType = typeof(Circuit).Assembly
        .GetType("Microsoft.AspNetCore.Components.Server.Circuits.CircuitId")
        ?? throw new InvalidOperationException("CircuitId type was not found.");

    private static readonly FieldInfo CircuitHostField = typeof(Circuit)
        .GetField("_circuitHost", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Circuit._circuitHost field was not found.");

    private static readonly FieldInfo CircuitIdField = CircuitHostType
        .GetField("<CircuitId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("CircuitHost.CircuitId backing field was not found.");
}
