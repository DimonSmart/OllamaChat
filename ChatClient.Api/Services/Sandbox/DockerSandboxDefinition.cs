using ChatClient.Application.Services.Sandbox;

namespace ChatClient.Api.Services.Sandbox;

public sealed class DockerSandboxDefinition : ISandboxDefinition
{
    public string Image { get; init; } = string.Empty;

    public string Network { get; init; } = "none";

    public double CpuLimit { get; init; } = 1;

    public int MemoryMb { get; init; } = 1024;

    public int PidsLimit { get; init; } = 256;

    public int CommandTimeoutSeconds { get; init; } = 600;

    public int MaxOutputKb { get; init; } = 64;

    public string User { get; init; } = "65534:65534";

    public bool ReadOnlyRoot { get; init; } = true;
}
