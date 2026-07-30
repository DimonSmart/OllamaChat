using ChatClient.Application.Services.Sandbox;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ChatClient.Api.Services.Sandbox;

public sealed class DockerSandboxProvider(
    ILogger<DockerSandboxProvider> logger) : ISandboxProvider
{
    internal const string ProviderType = "docker";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public string Type => ProviderType;

    public string DisplayName => "Docker";

    public string DefaultConfiguration =>
        """
        image: mcr.microsoft.com/dotnet/sdk:10.0-noble
        network: bridge
        cpuLimit: 1
        memoryMb: 1024
        pidsLimit: 256
        commandTimeoutSeconds: 600
        maxOutputKb: 64
        user: "65534:65534"
        readOnlyRoot: true
        """;

    public ISandboxDefinition ParseDefinition(string configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            throw new ArgumentException("Sandbox configuration is required.", nameof(configuration));
        }

        try
        {
            var values = ParseYamlMap(configuration);
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "image",
                "network",
                "cpuLimit",
                "memoryMb",
                "pidsLimit",
                "commandTimeoutSeconds",
                "maxOutputKb",
                "user",
                "readOnlyRoot"
            };
            var unknown = values.Keys.FirstOrDefault(key => !allowed.Contains(key));
            if (unknown is not null)
            {
                throw new ArgumentException($"Sandbox configuration contains unsupported property '{unknown}'.");
            }

            return Deserializer.Deserialize<DockerSandboxDefinition>(configuration);
        }
        catch (YamlException ex)
        {
            throw new ArgumentException(
                $"Sandbox configuration is invalid at line {ex.Start.Line}, column {ex.Start.Column}: {ex.Message}",
                nameof(configuration),
                ex);
        }
    }

    public SandboxDefinitionValidation ValidateDefinition(ISandboxDefinition definition)
    {
        if (definition is not DockerSandboxDefinition docker)
        {
            return SandboxDefinitionValidation.Failure(["Selected provider requires a Docker sandbox definition."]);
        }

        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(docker.Image))
        {
            errors.Add("Docker sandbox image is required.");
        }

        if (!string.Equals(docker.Network, "none", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(docker.Network, "bridge", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Docker sandbox network must be 'none' or 'bridge'.");
        }

        if (docker.CpuLimit is < 0.1 or > 64)
        {
            errors.Add("Docker sandbox CPU limit must be between 0.1 and 64.");
        }

        if (docker.MemoryMb is < 128 or > 65536)
        {
            errors.Add("Docker sandbox memory must be between 128 and 65536 MB.");
        }

        if (docker.PidsLimit is < 16 or > 4096)
        {
            errors.Add("Docker sandbox process limit must be between 16 and 4096.");
        }

        if (docker.CommandTimeoutSeconds is < 1 or > 3600)
        {
            errors.Add("Docker sandbox command timeout must be between 1 and 3600 seconds.");
        }

        if (docker.MaxOutputKb is < 4 or > 1024)
        {
            errors.Add("Docker sandbox max output must be between 4 and 1024 KB.");
        }

        if (!TryParseUser(docker.User, out _))
        {
            errors.Add("Docker sandbox user must use the 'UID:GID' format.");
        }

        return errors.Count == 0
            ? SandboxDefinitionValidation.Success
            : SandboxDefinitionValidation.Failure(errors);
    }

    public SandboxDefinitionSummary GetSummary(ISandboxDefinition definition)
    {
        var docker = (DockerSandboxDefinition)definition;
        var isRoot = TryParseUser(docker.User, out var user) && user.IsRoot;
        return new SandboxDefinitionSummary(
            docker.Image,
            $"{docker.CpuLimit:0.##} CPU / {docker.MemoryMb} MB / {docker.PidsLimit} processes",
            docker.Network,
            isRoot,
            isRoot ? "Container runs as root." : null);
    }

    public async Task<SandboxProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("info");
            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Docker could not be started.");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode == 0)
            {
                return new SandboxProviderAvailability(true);
            }

            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            return new SandboxProviderAvailability(false,
                string.IsNullOrWhiteSpace(error)
                    ? "Docker was found, but the Docker daemon is not available."
                    : "Docker was found, but the Docker daemon is not available.");
        }
        catch (Win32Exception)
        {
            return new SandboxProviderAvailability(
                false,
                "Docker is not available. Install and start Docker before using this sandbox profile.");
        }
        catch (InvalidOperationException)
        {
            return new SandboxProviderAvailability(
                false,
                "Docker was found, but the Docker daemon is not available.");
        }
    }

    public Task<ISandbox> CreateAsync(
        ISandboxDefinition definition,
        SandboxCreateContext context,
        CancellationToken cancellationToken = default)
    {
        var docker = definition as DockerSandboxDefinition
            ?? throw new InvalidOperationException("Docker provider requires a Docker sandbox definition.");
        var validation = ValidateDefinition(docker);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));
        }

        if (!TryParseUser(docker.User, out var user))
        {
            throw new InvalidOperationException("Docker sandbox user must use the 'UID:GID' format.");
        }

        var containerName = $"ollamachat-sandbox-{context.SessionId.ToLowerInvariant()}";
        logger.LogInformation(
            "Starting Docker sandbox. SessionId={SessionId}, Image={Image}, Network={Network}, CpuLimit={CpuLimit}, MemoryMb={MemoryMb}, PidsLimit={PidsLimit}, ContainerName={ContainerName}",
            context.SessionId,
            docker.Image,
            docker.Network,
            docker.CpuLimit,
            docker.MemoryMb,
            docker.PidsLimit,
            containerName);

        var executor = new DockerShellExecutor(BuildExecutorOptions(docker, context, user, containerName));

        return Task.FromResult<ISandbox>(new DockerSandbox(context.WorkspacePath, executor));
    }

    internal static DockerShellExecutorOptions BuildExecutorOptions(
        DockerSandboxDefinition definition,
        SandboxCreateContext context,
        ContainerUser user,
        string containerName)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);

        return new DockerShellExecutorOptions
        {
            Image = definition.Image,
            ContainerName = containerName,
            Mode = ShellMode.Persistent,
            HostWorkdir = context.WorkspacePath,
            ContainerWorkdir = DockerShellExecutor.DefaultContainerWorkdir,
            MountReadonly = false,
            Network = definition.Network,
            MemoryBytes = definition.MemoryMb * 1024L * 1024L,
            PidsLimit = definition.PidsLimit,
            User = user,
            ReadOnlyRoot = definition.ReadOnlyRoot,
            Timeout = TimeSpan.FromSeconds(definition.CommandTimeoutSeconds),
            MaxOutputBytes = definition.MaxOutputKb * 1024,
            ExtraRunArgs =
            [
                "--cpus",
                definition.CpuLimit.ToString(CultureInfo.InvariantCulture),
                "--label",
                "ollamachat.sandbox=true",
                "--label",
                $"ollamachat.session={context.SessionId}",
                "--label",
                $"ollamachat.profile-id={context.ProfileId:D}"
            ],
            Environment = new Dictionary<string, string>
            {
                ["HOME"] = $"{DockerShellExecutor.DefaultContainerWorkdir}/.sandbox-home",
                ["DOTNET_CLI_HOME"] = $"{DockerShellExecutor.DefaultContainerWorkdir}/.sandbox-home",
                ["NUGET_PACKAGES"] = $"{DockerShellExecutor.DefaultContainerWorkdir}/.nuget/packages",
                ["OLLAMACHAT_SESSION_ID"] = context.SessionId,
                ["OLLAMACHAT_SANDBOX_PROFILE"] = context.ProfileName
            }
        };
    }

    private static Dictionary<string, object?> ParseYamlMap(string configuration)
    {
        var deserializer = new DeserializerBuilder().Build();
        return deserializer.Deserialize<Dictionary<string, object?>>(configuration)
               ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryParseUser(string value, out ContainerUser user)
    {
        user = ContainerUser.Default;
        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        user = new ContainerUser(parts[0], parts[1]);
        return true;
    }
}
