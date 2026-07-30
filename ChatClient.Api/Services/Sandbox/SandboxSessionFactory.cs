using ChatClient.Application.Services;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.Sandbox;

namespace ChatClient.Api.Services.Sandbox;

public sealed class SandboxSessionFactory(
    ISandboxProfileService sandboxProfileService,
    ISandboxProviderRegistry sandboxProviderRegistry,
    ILogger<SandboxSessionFactory> logger) : ISandboxSessionFactory
{
    public async Task<SandboxSessionHandle> StartAsync(
        Guid profileId,
        string workspacePath,
        string sessionId,
        CancellationToken cancellationToken = default,
        IProgress<ChatSessionStartProgress>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            throw new InvalidOperationException("A workspace directory is required for shell-enabled agents.");
        }

        var normalizedWorkspacePath = Path.GetFullPath(workspacePath);
        var profile = await sandboxProfileService.GetByIdAsync(profileId)
            ?? throw new InvalidOperationException($"Sandbox profile '{profileId}' was not found.");
        var provider = sandboxProviderRegistry.GetRequired(profile.ProviderType);
        var definition = provider.ParseDefinition(profile.Configuration);
        var validation = provider.ValidateDefinition(definition);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));
        }

        progress?.Report(new ChatSessionStartProgress(
            ChatSessionStartStage.CheckingSandboxAvailability,
            $"Checking {provider.DisplayName} availability..."));
        var availability = await provider.CheckAvailabilityAsync(cancellationToken);
        if (!availability.IsAvailable)
        {
            throw new InvalidOperationException(
                availability.ErrorMessage ?? $"Sandbox provider '{profile.ProviderType}' is not available.");
        }

        var summary = provider.GetSummary(definition);
        var startedAt = Environment.TickCount64;
        logger.LogInformation(
            "Creating sandbox session. SessionId={SessionId}, ProfileId={ProfileId}, ProfileName={ProfileName}, ProviderType={ProviderType}, WorkspacePath={WorkspacePath}",
            sessionId,
            profile.Id,
            profile.Name,
            profile.ProviderType,
            normalizedWorkspacePath);

        ISandbox? sandbox = null;
        try
        {
            progress?.Report(new ChatSessionStartProgress(
                ChatSessionStartStage.StartingSandbox,
                $"Starting {provider.DisplayName} sandbox from {summary.Image}...{Environment.NewLine}The image may be downloaded on first launch."));
            sandbox = await provider.CreateAsync(
                definition,
                new SandboxCreateContext
                {
                    ProfileId = profile.Id,
                    SessionId = sessionId,
                    WorkspacePath = normalizedWorkspacePath,
                    ProfileName = profile.Name
                },
                cancellationToken);
            await sandbox.InitializeAsync(cancellationToken);
            progress?.Report(new ChatSessionStartProgress(
                ChatSessionStartStage.VerifyingSandbox,
                "Verifying sandbox workspace and shell..."));
            await RunStartupDiagnosticsAsync(sandbox, cancellationToken);
            progress?.Report(new ChatSessionStartProgress(
                ChatSessionStartStage.CreatingAgentSession,
                "Creating agent session..."));

            logger.LogInformation(
                "Sandbox session initialized. SessionId={SessionId}, ProfileId={ProfileId}, ProfileName={ProfileName}, ProviderType={ProviderType}, WorkspacePath={WorkspacePath}, ElapsedMs={ElapsedMs}",
                sessionId,
                profile.Id,
                profile.Name,
                profile.ProviderType,
                normalizedWorkspacePath,
                Environment.TickCount64 - startedAt);

            return new SandboxSessionHandle(() => DisposeSandboxAsync(
                sandbox,
                sessionId,
                profile.Id,
                profile.Name,
                profile.ProviderType,
                normalizedWorkspacePath))
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                ProviderType = profile.ProviderType,
                Summary = summary,
                WorkspacePath = normalizedWorkspacePath,
                Instance = sandbox
            };
        }
        catch
        {
            if (sandbox is not null)
            {
                await DisposeSandboxAsync(
                    sandbox,
                    sessionId,
                    profile.Id,
                    profile.Name,
                    profile.ProviderType,
                    normalizedWorkspacePath);
            }

            throw;
        }
    }

    private static async Task RunStartupDiagnosticsAsync(
        ISandbox sandbox,
        CancellationToken cancellationToken)
    {
        var result = await sandbox.ExecuteAsync(
            "test \"$(pwd)\" = \"/workspace\" && test -d /workspace && test -w /workspace && command -v bash >/dev/null",
            cancellationToken);
        if (result.ExitCode == 0)
        {
            return;
        }

        var error = (result.StandardError + Environment.NewLine + result.StandardOutput).Trim();
        if (error.Contains("bash", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The selected image does not contain bash required for persistent shell execution.");
        }

        throw new InvalidOperationException(
            "The sandbox user cannot write to the selected workspace. Adjust directory permissions or select a compatible container user.");
    }

    private async ValueTask DisposeSandboxAsync(
        ISandbox sandbox,
        string sessionId,
        Guid profileId,
        string profileName,
        string providerType,
        string workspacePath)
    {
        var startedAt = Environment.TickCount64;
        logger.LogInformation(
            "Disposing sandbox session. SessionId={SessionId}, ProfileId={ProfileId}, ProfileName={ProfileName}, ProviderType={ProviderType}, WorkspacePath={WorkspacePath}",
            sessionId,
            profileId,
            profileName,
            providerType,
            workspacePath);

        try
        {
            await sandbox.DisposeAsync();
            logger.LogInformation(
                "Sandbox session disposed. SessionId={SessionId}, ProfileId={ProfileId}, ProfileName={ProfileName}, ProviderType={ProviderType}, WorkspacePath={WorkspacePath}, ElapsedMs={ElapsedMs}",
                sessionId,
                profileId,
                profileName,
                providerType,
                workspacePath,
                Environment.TickCount64 - startedAt);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Sandbox session disposal failed. SessionId={SessionId}, ProfileId={ProfileId}, ProfileName={ProfileName}, ProviderType={ProviderType}, WorkspacePath={WorkspacePath}, ElapsedMs={ElapsedMs}",
                sessionId,
                profileId,
                profileName,
                providerType,
                workspacePath,
                Environment.TickCount64 - startedAt);
            throw;
        }
    }
}
