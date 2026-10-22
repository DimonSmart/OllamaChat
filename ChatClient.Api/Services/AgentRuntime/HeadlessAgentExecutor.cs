using ChatClient.Application.Services;
using ChatClient.Api.Client.Services.Agentic;
using Microsoft.Extensions.Logging.Abstractions;
using ChatClient.Application.Services.Agentic;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Application.Services.Sandbox;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.AgentRuntime;

public sealed record HeadlessAgentRunRequest(Guid AgentId, string Prompt, Guid? WorkspaceId = null);

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
    ILogger<HeadlessAgentExecutor>? logger = null) : IHeadlessAgentExecutor
{
    public async Task<HeadlessAgentRunResult> ExecuteAsync(
        HeadlessAgentRunRequest request, CancellationToken cancellationToken)
    {
        var reference = new AgentDefinitionReference(AgentDefinitionKind.SavedAgent, request.AgentId.ToString("D"));
        var descriptor = await catalog.FindAsync(reference, cancellationToken);
        if (descriptor is null)
            return HeadlessAgentRunResult.Fail(request.AgentId.ToString("D"), "agent_not_found", "Saved Agent was deleted or is unavailable.");

        var template = await templates.GetByIdAsync(request.AgentId);
        if (template is null)
            return HeadlessAgentRunResult.Fail(descriptor.Name, "agent_not_found", "Saved Agent was deleted or is unavailable.");

        // Resolve exactly once before runtime creation, never from a mutable definition mid-run.
        ResolvedWorkspace? workspace;
        try
        {
            if (request.WorkspaceId.HasValue && workspaceResolver is null)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "headless_capability_unsupported",
                    "Saved Workspace resolution is unavailable.");
            workspace = workspaceResolver is null ? null : await workspaceResolver.ResolveAsync(
                new WorkspaceSelection(WorkspaceId: request.WorkspaceId), cancellationToken);
        }
        catch (WorkspaceException ex)
        {
            return HeadlessAgentRunResult.Fail(descriptor.Name, ex.Code, ex.Message);
        }

        if (HasUnsupportedCapabilities(template, descriptor))
            return HeadlessAgentRunResult.Fail(descriptor.Name, "headless_capability_unsupported",
                "This Saved Agent uses capabilities that are not supported by headless execution.");

        FileAccessProviderProfile? pinnedFileProfile = null;
        AgentSkillsProfile? pinnedSkillsProfile = null;
        if (template.FileAccessProviderProfileId is { } fileId)
        {
            var profile = fileProfiles is null ? null : await fileProfiles.GetByIdAsync(fileId);
            if (profile is null)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "headless_capability_unsupported",
                    "The selected File Access profile is unavailable.");
            if (profile.AccessMode != FileAccessMode.ReadOnly)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "headless_capability_unsupported",
                    "Headless execution supports only read-only File Access.");
            if (profile.RequireReadApproval)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "interactive_approval_required",
                    "Headless File Access cannot request interactive read approval.");
            if (workspace is null)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "workspace_selection_invalid",
                    "A saved workspace is required for File Access.");
            pinnedFileProfile = new FileAccessProviderProfile
            {
                Id = profile.Id, Name = profile.Name, Instructions = profile.Instructions,
                AccessMode = profile.AccessMode, RequireReadApproval = profile.RequireReadApproval,
                RequireWriteApproval = profile.RequireWriteApproval
            };
        }

        var hasDiscoveredSkills = false;
        if (template.SkillsProviderProfileId is { } skillsId)
        {
            var profile = skillProfiles is null ? null : await skillProfiles.GetByIdAsync(skillsId);
            if (profile is null)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "headless_capability_unsupported",
                    "The selected Skills profile is unavailable.");
            if ((profile.IncludeClaudeSkills || profile.FileSources.Any(source =>
                !Path.IsPathFullyQualified(source.Directory))) && workspace is null)
                return HeadlessAgentRunResult.Fail(descriptor.Name, "workspace_selection_invalid",
                    "Workspace-relative Skills require a saved workspace.");
            pinnedSkillsProfile = new AgentSkillsProfile
            {
                Id = profile.Id, Name = profile.Name,
                IncludeClaudeSkills = profile.IncludeClaudeSkills,
                FileSources = profile.FileSources.Select(source => new SkillFileSource
                {
                    Directory = source.Directory, Patterns = source.Patterns.ToList()
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

        var overrides = new AgentSessionOverrides { WorkspacePath = workspace?.Path };
        var resolved = await resolver.ResolveAsync(reference,
            new AgentSessionDefinitionRequest { Overrides = overrides }, cancellationToken);
        if (!resolved.Validation.CanLaunch || resolved.DefaultModel is null)
            return HeadlessAgentRunResult.Fail(descriptor.Name, "agent_preflight_failed",
                "Saved Agent is invalid or its configured model is unavailable. Check the agent settings.");

        if (hasDiscoveredSkills && modelCapabilities is not null &&
            resolved.DefaultModel is { } skillsModel &&
            !await modelCapabilities.SupportsFunctionCallingAsync(skillsModel, cancellationToken))
            return HeadlessAgentRunResult.Fail(descriptor.Name, "agent_preflight_failed",
                "Selected model does not support function calling required by native Skills.");

        cancellationToken.ThrowIfCancellationRequested();
        var context = contextFactory.CreateRoot(descriptor, conversationId: null);
        var approval = new HeadlessToolApprovalCoordinator();
        var creation = new AgentRuntimeCreationContext
        {
            Configuration = new AppChatConfiguration(resolved.DefaultModel.ModelName, []),
            DefaultModel = resolved.DefaultModel,
            Overrides = overrides,
            PinnedDefinition = descriptor,
            PinnedSavedAgent = template.Clone(),
            RuntimeResources = new AgentSessionRuntimeResources
            {
                IsHeadlessExecution = true,
                WorkspacePath = workspace?.Path,
                HeadlessFileAccessProfile = pinnedFileProfile,
                HeadlessSkillsProfile = pinnedSkillsProfile,
                ToolApprovalCoordinator = approval,
                ToolApprovalPolicy = new SessionToolApprovalPolicy()
            }
        };
        var input = new AgentRuntimeRunRequest
        {
            InvocationKind = AgentRuntimeInvocationKind.UserMessage,
            Messages = [new AgentInputMessage(AgentMessageRole.User, request.Prompt)],
            Inputs = new Dictionary<string, string>(),
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

        if (approval.RequestCount != 0)
            return HeadlessAgentRunResult.Fail(descriptor.Name, "interactive_approval_required",
                "Agent requested interactive approval, which is not available for scheduled execution.", context.RunId);

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
