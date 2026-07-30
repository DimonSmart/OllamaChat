using ChatClient.Api.Services.Sandbox;
using ChatClient.Application.Services.Sandbox;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatClient.Tests;

public sealed class DockerSandboxProviderTests
{
    [Fact]
    public void DefaultConfiguration_UsesBridgeNetwork()
    {
        var provider = new DockerSandboxProvider(NullLogger<DockerSandboxProvider>.Instance);

        var definition = Assert.IsType<DockerSandboxDefinition>(
            provider.ParseDefinition(provider.DefaultConfiguration));

        Assert.Equal("bridge", definition.Network);
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(0.5, "0.5")]
    [InlineData(2.25, "2.25")]
    public void BuildExecutorOptions_MapsCpuLimitAndApplicationLabels(
        double cpuLimit,
        string expectedCpuValue)
    {
        var options = DockerSandboxProvider.BuildExecutorOptions(
            new DockerSandboxDefinition
            {
                Image = "mcr.microsoft.com/dotnet/sdk:10.0-noble",
                Network = "none",
                CpuLimit = cpuLimit,
                MemoryMb = 1024,
                PidsLimit = 256,
                CommandTimeoutSeconds = 600,
                MaxOutputKb = 64,
                User = "65534:65534",
                ReadOnlyRoot = true
            },
            new SandboxCreateContext
            {
                ProfileId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                SessionId = "session-123",
                WorkspacePath = "C:\\workspace",
                ProfileName = ".NET 10 Small"
            },
            new ContainerUser("65534", "65534"),
            "ollamachat-sandbox-session-123");

        Assert.Equal("ollamachat-sandbox-session-123", options.ContainerName);
        Assert.Equal("C:\\workspace", options.HostWorkdir);
        Assert.Equal(
            [
                "--cpus",
                expectedCpuValue,
                "--label",
                "ollamachat.sandbox=true",
                "--label",
                "ollamachat.session=session-123",
                "--label",
                "ollamachat.profile-id=11111111-1111-1111-1111-111111111111"
            ],
            options.ExtraRunArgs);
    }
}
