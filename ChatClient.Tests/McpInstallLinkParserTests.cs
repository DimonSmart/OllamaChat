using System.Text.Json;

using ChatClient.Api.Services;
using ChatClient.Application.Helpers;
using ChatClient.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ChatClient.Tests;

public class McpInstallLinkParserTests
{
    [Fact]
    public void Parse_StdIoServerLink_ReturnsCommandConfiguration()
    {
        var payload = JsonSerializer.Serialize(new
        {
            name = "memory",
            command = "uvx",
            args = new[] { "mcp-server" }
        });

        var link = $"vscode:mcp/install?{Uri.EscapeDataString(payload)}";

        var result = McpInstallLinkParser.Parse(link);

        Assert.Equal("memory", result.Name);
        Assert.Equal("uvx", result.Command);
        Assert.NotNull(result.Arguments);
        Assert.Single(result.Arguments);
        Assert.Equal("mcp-server", result.Arguments![0]);
        Assert.Null(result.Sse);
    }

    [Fact]
    public void Parse_RemoteServerLink_ReturnsSseConfiguration()
    {
        var payload = JsonSerializer.Serialize(new
        {
            name = "remote",
            type = "sse",
            url = "https://example.com/mcp"
        });

        var link = $"vscode:mcp/install?{Uri.EscapeDataString(payload)}";

        var result = McpInstallLinkParser.Parse(link);

        Assert.Equal("remote", result.Name);
        Assert.Equal("https://example.com/mcp", result.Sse);
        Assert.Null(result.Command);
        Assert.Null(result.Arguments);
    }

    [Theory]
    [InlineData("https://example.com/mcp")]
    [InlineData("mailto:test@example.com")]
    public void Parse_InvalidScheme_Throws(string link)
    {
        Assert.Throws<InvalidOperationException>(() => McpInstallLinkParser.Parse(link));
    }

    [Fact]
    public void Parse_InvalidJson_Throws()
    {
        var link = "vscode:mcp/install?not-json";

        Assert.Throws<InvalidOperationException>(() => McpInstallLinkParser.Parse(link));
    }

    [Fact]
    public async Task InstallFromLinkAsync_SavesConfiguration()
    {
        var payload = JsonSerializer.Serialize(new
        {
            name = "gallery",
            command = "uvx",
            args = new[] { "server" }
        });

        var link = $"vscode:mcp/install?{Uri.EscapeDataString(payload)}";

        var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["McpServers:FilePath"] = tempFile
                })
                .Build();

            using var loggerFactory = LoggerFactory.Create(static _ => { });
            var repository = new McpServerConfigRepository(configuration, loggerFactory.CreateLogger<McpServerConfigRepository>());
            var service = new McpServerConfigService(repository);

            var installed = await service.InstallFromLinkAsync(link);

            Assert.False(string.IsNullOrWhiteSpace(installed.Name));
            Assert.NotNull(installed.Id);

            var all = await service.GetAllAsync();
            Assert.True(all.Count >= 1);
            var saved = Assert.Single(all, s => s.Id == installed.Id);
            Assert.Equal(installed.Id, saved.Id);
            Assert.Equal("gallery", saved.Name);
            Assert.Equal("uvx", saved.Command);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}

