using ChatClient.Api.Services;
using ChatClient.Api.Services.BuiltIn;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatClient.Tests;

public class McpServerConfigServiceBuiltInTests
{
    [Fact]
    public async Task GetAllAsync_EmptyStorage_AddsBuiltInServers()
    {
        await using var fixture = new TestFixture();
        var service = fixture.CreateService();

        var servers = await service.GetAllAsync();

        Assert.Equal(BuiltInMcpServerCatalog.Definitions.Count, servers.Count);
        foreach (var definition in BuiltInMcpServerCatalog.Definitions)
        {
            var server = Assert.Single(servers, s => s.Id == definition.Id);
            Assert.True(server.IsBuiltIn);
            Assert.Equal(definition.Key, server.BuiltInKey);
            Assert.Equal(definition.Name, server.Name);
        }
    }

    [Fact]
    public async Task DeleteAsync_BuiltInServer_Throws()
    {
        await using var fixture = new TestFixture();
        var service = fixture.CreateService();
        var builtIn = (await service.GetAllAsync()).First();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(builtIn.Id!.Value));

        Assert.Contains("cannot be deleted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAsync_BuiltInServer_Throws()
    {
        await using var fixture = new TestFixture();
        var service = fixture.CreateService();
        var builtIn = (await service.GetAllAsync()).First();

        builtIn.Name = "Hacked Name";
        builtIn.Command = "should-not-be-saved";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(builtIn));

        Assert.Contains("cannot be edited", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly string _tempFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(static _ => { });

        public McpServerConfigService CreateService()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["McpServers:FilePath"] = _tempFilePath
                })
                .Build();

            var repository = new McpServerConfigRepository(
                configuration,
                _loggerFactory.CreateLogger<McpServerConfigRepository>());

            return new McpServerConfigService(repository);
        }

        public ValueTask DisposeAsync()
        {
            if (File.Exists(_tempFilePath))
            {
                File.Delete(_tempFilePath);
            }

            _loggerFactory.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
