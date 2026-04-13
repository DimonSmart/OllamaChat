using ChatClient.Api.PlanningRuntime.Agents;
using ChatClient.Api.Services;
using System.Text.Json;

namespace ChatClient.Api.PlanningRuntime.Planning;

/// <summary>
/// Planner-facing kind of a capability.
/// </summary>
public enum CapabilityKind
{
    /// <summary>Invokes a registered MCP tool.</summary>
    Tool,

    /// <summary>Delegates to a preconfigured saved agent.</summary>
    Agent
}

/// <summary>
/// Side-effect profile of a capability, derived from MCP tool annotations.
/// </summary>
public enum CapabilitySideEffectProfile
{
    /// <summary>Read-only, no external state is mutated.</summary>
    ReadOnly,

    /// <summary>Mutates external state but can be called more than once safely.</summary>
    Idempotent,

    /// <summary>Mutates external state and is not safe to call more than once.</summary>
    Destructive,

    /// <summary>Produces only a side effect with no structured output.</summary>
    SideEffect
}

/// <summary>
/// Planner-facing typed description of a single capability.
/// Provides structured access to input/output schemas, side-effect profile,
/// and planning metadata without exposing low-level execution details.
/// </summary>
public sealed class CapabilityDescriptor
{
    /// <summary>Unique identifier used in step <c>capabilityId</c>.</summary>
    public required string CapabilityId { get; init; }

    /// <summary>Human-readable display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Purpose description used by the planner when selecting capabilities.</summary>
    public required string Description { get; init; }

    /// <summary>Whether this capability is a tool or a saved agent.</summary>
    public required CapabilityKind Kind { get; init; }

    /// <summary>JSON Schema for inputs. Null for agents (schema is internal).</summary>
    public JsonElement? InputSchema { get; init; }

    /// <summary>JSON Schema for outputs. Null when the tool does not declare one.</summary>
    public JsonElement? OutputSchema { get; init; }

    /// <summary>Side-effect profile, used to decide whether fan-out or retry is safe.</summary>
    public CapabilitySideEffectProfile SideEffectProfile { get; init; }

    /// <summary>Whether this capability may produce different results on each call (open-world).</summary>
    public bool OpenWorld { get; init; }

    /// <summary>Whether this capability may pause execution to ask the user for input.</summary>
    public bool MayRequireUserInput { get; init; }

    /// <summary>
    /// Planner role hint: discover, acquire, transform, or act.
    /// Null when not inferred or not applicable.
    /// </summary>
    public AppToolPlannerRole? PlannerRole { get; init; }

    /// <summary>
    /// Kind of output this capability produces: reference, document, structured_data, side_effect.
    /// Null when not inferred or not applicable.
    /// </summary>
    public AppToolProducesKind? ProducesKind { get; init; }

    /// <summary>Additional planning guidance from tool metadata.</summary>
    public AppToolPlanningMetadata? PlanningMetadata { get; init; }

    /// <summary>
    /// Builds a <see cref="CapabilityDescriptor"/> from an <see cref="AppToolDescriptor"/>.
    /// </summary>
    public static CapabilityDescriptor FromTool(AppToolDescriptor tool) => new()
    {
        CapabilityId = tool.QualifiedName,
        DisplayName = tool.DisplayName,
        Description = tool.PlanningMetadata?.Purpose ?? tool.Description,
        Kind = CapabilityKind.Tool,
        InputSchema = tool.InputSchema,
        OutputSchema = tool.OutputSchema,
        SideEffectProfile = ResolveSideEffectProfile(tool),
        OpenWorld = tool.OpenWorldHint,
        MayRequireUserInput = tool.MayRequireUserInput,
        PlannerRole = tool.PlanningMetadata?.PlannerRole,
        ProducesKind = tool.PlanningMetadata?.ProducesKind,
        PlanningMetadata = tool.PlanningMetadata
    };

    /// <summary>
    /// Builds a <see cref="CapabilityDescriptor"/> from a <see cref="PlanningCallableAgentDescriptor"/>.
    /// </summary>
    public static CapabilityDescriptor FromAgent(PlanningCallableAgentDescriptor agent) => new()
    {
        CapabilityId = agent.Name,
        DisplayName = agent.DisplayName,
        Description = agent.Description,
        Kind = CapabilityKind.Agent,
        InputSchema = null,
        OutputSchema = null,
        SideEffectProfile = CapabilitySideEffectProfile.ReadOnly,
        OpenWorld = false,
        MayRequireUserInput = false,
        PlannerRole = null,
        ProducesKind = null,
        PlanningMetadata = null
    };

    private static CapabilitySideEffectProfile ResolveSideEffectProfile(AppToolDescriptor tool)
    {
        if (tool.DestructiveHint)
            return CapabilitySideEffectProfile.Destructive;
        if (tool.ReadOnlyHint)
            return CapabilitySideEffectProfile.ReadOnly;
        if (tool.IdempotentHint)
            return CapabilitySideEffectProfile.Idempotent;

        return tool.PlanningMetadata?.ProducesKind == AppToolProducesKind.SideEffect
            ? CapabilitySideEffectProfile.SideEffect
            : CapabilitySideEffectProfile.ReadOnly;
    }
}

/// <summary>
/// Typed catalog of all capabilities available to the planner for a single run.
/// Combines tool and agent capabilities into one typed collection,
/// providing structured access to schemas, side-effect profiles, and planning metadata.
/// </summary>
public sealed class CapabilityCatalog
{
    private readonly Dictionary<string, CapabilityDescriptor> _byId;

    private CapabilityCatalog(IEnumerable<CapabilityDescriptor> capabilities)
    {
        _byId = capabilities.ToDictionary(
            static c => c.CapabilityId,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a catalog from the raw tool and agent collections used by the current planner.
    /// </summary>
    public static CapabilityCatalog From(
        IReadOnlyCollection<AppToolDescriptor> tools,
        IReadOnlyCollection<PlanningCallableAgentDescriptor> agents)
    {
        var capabilities = tools.Select(CapabilityDescriptor.FromTool)
            .Concat(agents.Select(CapabilityDescriptor.FromAgent));
        return new CapabilityCatalog(capabilities);
    }

    /// <summary>All capabilities in the catalog, ordered by id.</summary>
    public IReadOnlyCollection<CapabilityDescriptor> All =>
        _byId.Values
            .OrderBy(static c => c.CapabilityId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>All tool capabilities.</summary>
    public IReadOnlyCollection<CapabilityDescriptor> Tools =>
        _byId.Values
            .Where(static c => c.Kind == CapabilityKind.Tool)
            .OrderBy(static c => c.CapabilityId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>All agent capabilities.</summary>
    public IReadOnlyCollection<CapabilityDescriptor> Agents =>
        _byId.Values
            .Where(static c => c.Kind == CapabilityKind.Agent)
            .OrderBy(static c => c.CapabilityId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Returns the capability with the given id, or null if not found.</summary>
    public CapabilityDescriptor? TryGet(string capabilityId) =>
        _byId.TryGetValue(capabilityId, out var descriptor) ? descriptor : null;

    /// <summary>Returns the capability with the given id, or throws if not found.</summary>
    public CapabilityDescriptor GetRequired(string capabilityId) =>
        TryGet(capabilityId)
        ?? throw new InvalidOperationException($"Capability '{capabilityId}' is not registered.");
}
