using ChatClient.Api.Services;
using Microsoft.Extensions.Hosting;

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
        var handler = new AutoShutdownCircuitHandler(lifetime);

        await handler.OnCircuitOpenedAsync(null!, default);
        await handler.OnCircuitClosedAsync(null!, default);
        Assert.False(lifetime.Stopped);

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.True(lifetime.Stopped);
    }

    [Fact]
    public async Task ReconnectWithinDelay_DoesNotStop()
    {
        var lifetime = new TestLifetime();
        var handler = new AutoShutdownCircuitHandler(lifetime);

        await handler.OnCircuitOpenedAsync(null!, default);
        await handler.OnCircuitClosedAsync(null!, default);

        await Task.Delay(TimeSpan.FromMilliseconds(100));
        await handler.OnCircuitOpenedAsync(null!, default);
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.False(lifetime.Stopped);
    }
}
