using ChatClient.Application.Services;
using ChatClient.Api.Client.Services.Agentic;
using Microsoft.Extensions.Logging.Abstractions;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Application.Services.Sandbox;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.AgentRuntime;

public sealed record HeadlessAgentRunRequest(
    Guid AgentId,
    string Prompt,
    Guid? WorkspaceId = null,
    string? DirectPath = null,
    Guid? WorkflowId = null,
    AgentExecutionPolicy? ExecutionPolicy = null,
    Guid? SandboxProfileId = null,
    IReadOnlyDictionary<string, string>? Inputs = null,
    ServerModelSelection? ModelOverride = null);

public sealed record HeadlessAgentRunResult(
    string AgentName,
    string? AgentRunId,
    string? Text,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => ErrorCode is null;
    public static HeadlessAgentRunResult Fail(string name, string code, string message, string? runId = null)
        => new(name, runId, null, code, message);
}

public interface IHeadlessAgentExecutor
{
    Task<HeadlessAgentRunResult> ExecuteAsync(
        HeadlessAgentRunRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Each instance belongs to a new execution DI scope. No chat session, saved-chat
/// service, Blazor circuit, or conversation history is ever constructed.
/// </summary>
public sealed class HeadlessAgentExecutor(
    IAgentTemplateService templates,
    IAgentDefinitionCatalog catalog,
    IAgentSessionDefinitionResolver resolver,
    IAgentRunContextFactory contextFactory,
    IAgentRunner runner,
    IWorkspaceResolver? workspaceResolver = null,
    IFileAccessProviderProfileService? fileProfiles = null,
    IAgentSkillsProfileService? skillProfiles = null,
    IAgentLaunchCapabilityValidator? modelCapabilities = null,
    ILogger<HeadlessAgentExecutor>? logger = null,
    ISandboxSessionFactory? sandboxSessionFactory = null) : IHeadlessAgentExecutor
{
    public async Task<HeadlessAgentRunResult> ExecuteAsync(
        HeadlessAgentRunRequest request, CancellationToken cancellationToken)
    {
        var policy = request.ExecutionPolicy ?? AgentExecutionPolicy.ScheduledRestricted;
        var isCli = policy.Mode == AgentExecutionMode.CliNonInteractive;
        var isWorkflow = request.WorkflowId.HasValue;
        if (isWorkflow && !isCli)
            return HeadlessAgentRunResult.Fail("workflow", "headless_capability_unsupported",
                "Saved Workflows are not permitted by the scheduled execution policy.");

        var reference = isWorkflow
            ? new AgentDefinitionReference(AgentDefinitionKind.SavedWorkflow, request.WorkflowId!.Value.ToString("D"))
            : new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, request.AgentId.ToString("D"));
        var descriptor = await catalog.FindAsync(reference, cancellationToken);
        if (descriptor is null)
            return HeadlessAgentRunResult.Fail(reference.Id, "agent_not_found",
                "Saved definition was deleted or is unavailable.");

        AgentTemplateDefinition? template = null;
        if (!isWorkflow)
        {
            template = await templates.GetByIdAsync(request.AgentId);
            if (template is null)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "agent_not_found",
                    "Saved Agent was deleted or is unavailable.");
        }

        ResolvedWorkspace? workspace;
        try
        {
            if ((request.WorkspaceId.HasValue || request.DirectPath is not null) && workspaceResolver is null)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "workspace_selection_invalid",
                    "Workspace resolution is unavailable.");
            workspace = workspaceResolver is null ? null : await workspaceResolver.ResolveAsync(
                new WorkspaceSelection(WorkspaceId: request.WorkspaceId, DirectPath: request.DirectPath), cancellationToken);
        }
        catch (WorkspaceException ex)
        {
            return HeadlessAgentRunResult.Fail(descriptor.Name, ex.Code, ex.Message);
        }

        if (!isCli && template is not null && HasUnsupportedCapabilities(template, descriptor))
            return HeadlessAgentRunResult.Fail(descriptor.Name, "headless_capability_unsupported",
                "This Saved Agent uses capabilities that are not supported by scheduled execution.");

        FileAccessProviderProfile? pinnedFileProfile = null;
        AgentSkillsProfile? pinnedSkillsProfile = null;
        if (template?.FileAccessProviderProfileId is { } fileId)
        {
            var profile = fileProfiles is null ? null : await fileProfiles.GetByIdAsync(fileId);
            if (profile is null)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "headless_capability_unsupported",
                    "The selected File Access profile is unavailable.");
            if (!isCli && profile.AccessMode != FileAccessMode.ReadOnly)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "headless_capability_unsupported",
                    "Scheduled execution supports only read-only File Access.");
            if (isCli && !policy.AllowRead)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "policy_read_denied",
                    "File Access requires --allow-read (or --allow-write).");
            if (isCli && profile.AccessMode != FileAccessMode.ReadOnly && !policy.AllowWrite)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "policy_write_denied",
                    "ReadWrite File Access requires --allow-write.");
            if (!isCli && profile.RequireReadApproval)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "interactive_approval_required",
                    "Scheduled File Access cannot request interactive read approval.");
            if (workspace is null)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "workspace_selection_invalid",
                    "A workspace is required for File Access.");
            pinnedFileProfile = new FileAccessProviderProfile
            {
                Id = profile.Id,
                Name = profile.Name,
                Instructions = profile.Instructions,
                AccessMode = profile.AccessMode,
                RequireReadApproval = isCli ? false : profile.RequireReadApproval,
                RequireWriteApproval = isCli ? false : profile.RequireWriteApproval
            };
        }

        var hasDiscoveredSkills = false;
        if (template?.SkillsProviderProfileId is { } skillsId)
        {
            var profile = skillProfiles is null ? null : await skillProfiles.GetByIdAsync(skillsId);
            if (profile is null)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "headless_capability_unsupported",
                    "The selected Skills profile is unavailable.");
            if ((profile.IncludeClaudeSkills || profile.FileSources.Any(source =>
                !Path.IsPathFullyQualified(source.Directory))) && workspace is null)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "workspace_selection_invalid",
                    "Workspace-relative Skills require a workspace.");
            pinnedSkillsProfile = new AgentSkillsProfile
            {
                Id = profile.Id,
                Name = profile.Name,
                IncludeClaudeSkills = profile.IncludeClaudeSkills,
                FileSources = profile.FileSources.Select(source => new SkillFileSource
                {
                    Directory = source.Directory,
                    Patterns = source.Patterns.ToList()
                }).ToList()
            };
            var discovered = await AgentSkillsDiscovery.DiscoverAsync(pinnedSkillsProfile, workspace?.Path,
                logger ?? NullLogger<HeadlessAgentExecutor>.Instance, cancellationToken);
            try
            {
                hasDiscoveredSkills = discovered.Skills.Count > 0;
                if (hasDiscoveredSkills && modelCapabilities is null)
                    return HeadlessAgentRunResult.Fail(descriptor.Name, "headless_capability_unsupported",
                        "Native Skills function-calling validation is unavailable.");
            }
            finally { discovered.Source?.Dispose(); }
        }

        if (isCli && template?.EnableShell == true && !policy.AllowShell)
            return HeadlessAgentRunResult.Fail(descriptor.Name, "policy_shell_denied",
                "Shell execution requires --allow-shell.");
        if (isCli && template?.EnableFileMemory == true && !policy.AllowFileMemory)
            return HeadlessAgentRunResult.Fail(descriptor.Name, "policy_memory_denied",
                "File Memory requires --allow-file-memory.");

        if (isCli && descriptor.LaunchCapabilities.SupportsSandboxProfile)
        {
            if (!policy.AllowShell)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "policy_shell_denied",
                    "Shell execution requires --allow-shell.");
            if (!policy.AllowWrite)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "policy_write_denied",
                    "Sandbox shell can modify its mounted workspace; also specify --allow-write.");
        }

        var inputs = new Dictionary<string, string>(request.Inputs ??
            new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        if (isWorkflow)
        {
            foreach (var inputDefinition in descriptor.Inputs)
            {
                if (!inputs.ContainsKey(inputDefinition.Key) && inputDefinition.DefaultValue is { } defaultValue)
                    inputs[inputDefinition.Key] = defaultValue;
            }
        }

        var overrides = new AgentSessionOverrides
        {
            WorkspacePath = workspace?.Path,
            SandboxProfileId = request.SandboxProfileId
        };
        var resolved = await resolver.ResolveAsync(reference,
            new AgentSessionDefinitionRequest
            {
                Overrides = overrides,
                Inputs = inputs,
                UiModelSelection = request.ModelOverride ?? new ServerModelSelection(null, null)
            }, cancellationToken);
        if (!resolved.Validation.CanLaunch ||
            (descriptor.ModelRequirement == AgentModelRequirement.Required && resolved.DefaultModel is null))
        {
            var reason = resolved.Validation.Problems.FirstOrDefault()?.Message ??
                "Saved definition or its configured model is unavailable.";
            return HeadlessAgentRunResult.Fail(descriptor.Name, "agent_preflight_failed", reason);
        }

        if (hasDiscoveredSkills && modelCapabilities is not null &&
            resolved.DefaultModel is { } skillsModel &&
            !await modelCapabilities.SupportsFunctionCallingAsync(skillsModel, cancellationToken))
            return HeadlessAgentRunResult.Fail(descriptor.Name, "agent_preflight_failed",
                "Selected model does not support function calling required by native Skills.");

        cancellationToken.ThrowIfCancellationRequested();
        var context = contextFactory.CreateRoot(descriptor, conversationId: null);
        var approval = new HeadlessToolApprovalCoordinator();
        SandboxSessionHandle? sandboxHandle = null;
        try
        {
            if (isCli && descriptor.LaunchCapabilities.SupportsSandboxProfile)
            {
                if (request.SandboxProfileId is not Guid sandboxId || workspace is null || sandboxSessionFactory is null)
                    return HeadlessAgentRunResult.Fail(descriptor.Name, "agent_preflight_failed",
                        "Shell execution requires a valid workspace and Sandbox Profile.");
                try
                {
                    sandboxHandle = await sandboxSessionFactory.StartAsync(
                        sandboxId, workspace.Path, context.RunId, cancellationToken);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                    logger?.LogWarning("CLI sandbox preflight failed. RunId={RunId}, Reason={Type}",
                        context.RunId, ex.GetType().Name);
                    return HeadlessAgentRunResult.Fail(descriptor.Name, "sandbox_unavailable",
                        "The selected Sandbox Profile is unavailable or invalid.", context.RunId);
                }
            }

            var creation = new AgentRuntimeCreationContext
            {
                Configuration = new AppChatConfiguration(resolved.DefaultModel?.ModelName ?? string.Empty, []),
                DefaultModel = resolved.DefaultModel,
                Overrides = overrides,
                PinnedDefinition = descriptor,
                PinnedSavedAgent = template?.Clone(),
                RuntimeResources = new AgentSessionRuntimeResources
                {
                    IsHeadlessExecution = true,
                    ExecutionPolicy = policy,
                    RootExecutionId = context.RunId,
                    RootSavedAgentId = template?.Id,
                    WorkspacePath = workspace?.Path,
                    Sandbox = sandboxHandle?.Instance,
                    HeadlessFileAccessProfile = pinnedFileProfile,
                    HeadlessSkillsProfile = pinnedSkillsProfile,
                    ToolApprovalCoordinator = approval,
                    ToolApprovalPolicy = new SessionToolApprovalPolicy()
                }
            };
            var runOnStart = isWorkflow && descriptor.LaunchBehavior == AgentLaunchBehavior.RunOnStart &&
                string.IsNullOrWhiteSpace(request.Prompt);
            if (!runOnStart && string.IsNullOrWhiteSpace(request.Prompt))
                return HeadlessAgentRunResult.Fail(descriptor.Name, "invalid_input",
                    "A non-empty user prompt is required.");
            var input = new AgentRuntimeRunRequest
            {
                InvocationKind = runOnStart
                    ? AgentRuntimeInvocationKind.RunOnStart
                    : AgentRuntimeInvocationKind.UserMessage,
                Messages = runOnStart
                    ? []
                    : [new AgentInputMessage(AgentMessageRole.User, request.Prompt)],
                Inputs = inputs,
                Attachments = []
            };

            AgentRunEvent? terminal = null;
            await foreach (var runEvent in runner.RunAsync(reference, input, creation, context, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                if (runEvent is AgentRunCompleted or AgentRunFailed)
                {
                    if (terminal is not null)
                        return HeadlessAgentRunResult.Fail(descriptor.Name, "runtime_protocol_violation",
                            "Agent returned multiple terminal events.", context.RunId);
                    terminal = runEvent;
                }
                else if (terminal is not null)
                {
                    return HeadlessAgentRunResult.Fail(descriptor.Name, "runtime_protocol_violation",
                        "Agent emitted an event after completion.", context.RunId);
                }
            }

            if (!isCli && approval.RequestCount != 0)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "interactive_approval_required",
                    "Agent requested interactive approval, unavailable for scheduled execution.", context.RunId);

            return terminal switch
            {
                AgentRunCompleted completed => new HeadlessAgentRunResult(
                    descriptor.Name, context.RunId, completed.Result.FinalMessage.Content, null, null),
                AgentRunFailed failed => HeadlessAgentRunResult.Fail(
                    descriptor.Name, failed.Error.Code, "Agent failed: " + SafeErrorCode(failed.Error.Code), context.RunId),
                _ => HeadlessAgentRunResult.Fail(descriptor.Name, "runtime_protocol_violation",
                    "Agent returned no terminal event.", context.RunId)
            };
        }
        finally
        {
            if (sandboxHandle is not null)
                await sandboxHandle.DisposeAsync();
        }
    }

    private static string SafeErrorCode(string code)
        => string.IsNullOrWhiteSpace(code) ? "execution_failed"
            : new string(code.Where(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').Take(80).ToArray());

    public static bool HasUnsupportedCapabilities(AgentTemplateDefinition template, AgentDefinitionDescriptor descriptor) =>
        descriptor.LaunchCapabilities.SupportsSandboxProfile ||
        descriptor.DefaultMcpServerBindings.Count != 0 ||
        template.McpServerBindings.Count != 0 ||
        template.EnableShell || template.EnableFileMemory ||
         template.TodoProviderProfileId.HasValue ||
        template.AgentModeProviderProfileId.HasValue ||
        template.CompactionProfileId.HasValue ||
        template.RagProviderProfileId.HasValue ||
        template.BackgroundAgentIds.Count != 0 ||
        template.KnowledgeStoreIds.Count != 0 ||
        template.ContinueUntilTodosComplete;
}

/// <summary>Run-local, noninteractive, and incapable of granting any approval.</summary>
public sealed class HeadlessToolApprovalCoordinator : ISessionToolApprovalCoordinator
{
    private int _requests;
    public int RequestCount => Volatile.Read(ref _requests);
    public SessionToolApprovalRequest? PendingRequest => null;
    public event Action? PendingRequestChanged { add { } remove { } }

    public Task<ToolApprovalDecision> RequestApprovalAsync(
        SessionToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _requests);
        return Task.FromResult(ToolApprovalDecision.Deny);
    }

    public bool TryRespond(string requestId, ToolApprovalDecision decision,
        Action<SessionToolApprovalRequest> beforeCompletion) => false;

    public void CancelPending() { }
}
