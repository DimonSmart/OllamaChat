using ChatClient.Application.Helpers;
using ChatClient.Application.Services;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Application.Services.Sandbox;
using ChatClient.Domain.Models;
using System.Globalization;
using System.Text.Json;

namespace ChatClient.Application.Services.Agentic;

public sealed record AgentSessionDefinitionRequest
{
    public ServerModelSelection UiModelSelection { get; init; } = new(null, null);

    public IReadOnlyDictionary<string, string> Inputs { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public AgentSessionOverrides Overrides { get; init; } = new();
}

public sealed record AgentSessionOverrides
{
    public IReadOnlyList<McpServerSessionBinding>? McpServerBindings { get; init; }
    public string? WorkspacePath { get; init; }
    public Guid? SandboxProfileId { get; init; }

    public AgentSessionOverrides Snapshot() =>
        new()
        {
            McpServerBindings = McpServerBindings?
                .Select(static binding => binding.Clone())
                .ToList(),
            WorkspacePath = WorkspacePath,
            SandboxProfileId = SandboxProfileId
        };
}

public sealed record ChatRuntimeParticipantDescriptor
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public string AvatarText { get; init; } = string.Empty;
    public required AgentRuntimeKind RuntimeKind { get; init; }
}

public sealed record ResolvedAgentSessionDefinition
{
    public required AgentDefinitionDescriptor Descriptor { get; init; }
    public required AgentDefinitionReference RuntimeReference { get; init; }
    public ServerModel? DefaultModel { get; init; }
    public IReadOnlyDictionary<string, string> Inputs { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public required ChatRuntimeParticipantDescriptor PresentationParticipant { get; init; }
    public required AgentDefinitionLaunchValidation Validation { get; init; }
}

public interface IAgentSessionDefinitionResolver
{
    Task<AgentDefinitionLaunchValidation> ValidateAsync(
        AgentDefinitionReference reference,
        AgentSessionDefinitionRequest request,
        CancellationToken cancellationToken = default);

    Task<ResolvedAgentSessionDefinition> ResolveAsync(
        AgentDefinitionReference reference,
        AgentSessionDefinitionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class AgentSessionDefinitionResolver(
    IAgentDefinitionCatalog catalog,
    IWorkflowDefinitionPreflightValidator workflowPreflightValidator,
    IAgentLaunchCapabilityValidator launchCapabilityValidator,
    ISandboxProfileService sandboxProfileService,
    ISandboxProviderRegistry sandboxProviderRegistry) : IAgentSessionDefinitionResolver
{
    public async Task<AgentDefinitionLaunchValidation> ValidateAsync(
        AgentDefinitionReference reference,
        AgentSessionDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = await catalog.FindAsync(reference, cancellationToken);
        if (descriptor is null)
        {
            return new AgentDefinitionLaunchValidation
            {
                CanLaunch = false,
                Problems = [new AgentDefinitionLaunchProblem($"Saved definition '{reference.Kind}:{reference.Id}' was not found.")]
            };
        }

        var (validation, _) = await ValidateLaunchAsync(
            reference,
            descriptor,
            request,
            cancellationToken);
        return validation;
    }

    public async Task<ResolvedAgentSessionDefinition> ResolveAsync(
        AgentDefinitionReference reference,
        AgentSessionDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = await catalog.FindAsync(reference, cancellationToken);
        if (descriptor is null)
        {
            return new ResolvedAgentSessionDefinition
            {
                Descriptor = new AgentDefinitionDescriptor
                {
                    Reference = reference,
                    Name = reference.Id,
                    RuntimeKind = AgentRuntimeKind.LlmAgent,
                    ModelRequirement = AgentModelRequirement.Required,
                    DefinitionProblems =
                    [
                        new AgentDefinitionProblem($"Saved definition '{reference.Kind}:{reference.Id}' was not found.")
                    ]
                },
                RuntimeReference = reference,
                PresentationParticipant = new ChatRuntimeParticipantDescriptor
                {
                    Id = reference.Id,
                    Name = reference.Id,
                    RuntimeKind = AgentRuntimeKind.LlmAgent
                },
                Validation = new AgentDefinitionLaunchValidation
                {
                    CanLaunch = false,
                    Problems = [new AgentDefinitionLaunchProblem($"Saved definition '{reference.Kind}:{reference.Id}' was not found.")]
                }
            };
        }

        var (validation, model) = await ValidateLaunchAsync(
            reference,
            descriptor,
            request,
            cancellationToken);

        return new ResolvedAgentSessionDefinition
        {
            Descriptor = descriptor,
            RuntimeReference = reference,
            DefaultModel = model,
            Inputs = descriptor.Inputs
                .Where(input => request.Inputs.TryGetValue(input.Key, out var value) && !string.IsNullOrWhiteSpace(value))
                .ToDictionary(input => input.Key, input => request.Inputs[input.Key], StringComparer.OrdinalIgnoreCase),
            PresentationParticipant = new ChatRuntimeParticipantDescriptor
            {
                Id = reference.Id,
                Name = descriptor.Name,
                Description = descriptor.Description,
                AvatarText = descriptor.AvatarText,
                RuntimeKind = descriptor.RuntimeKind
            },
            Validation = validation
        };
    }

    private async Task<(AgentDefinitionLaunchValidation Validation, ServerModel? Model)> ValidateLaunchAsync(
        AgentDefinitionReference reference,
        AgentDefinitionDescriptor descriptor,
        AgentSessionDefinitionRequest request,
        CancellationToken cancellationToken)
    {
        var problems = descriptor.DefinitionProblems
            .Select(static problem => new AgentDefinitionLaunchProblem(problem.Message))
            .Concat(ValidateInputs(descriptor.Inputs, request.Inputs))
            .ToList();
        if (descriptor.LaunchCapabilities.SupportsWorkspace)
        {
            if (string.IsNullOrWhiteSpace(request.Overrides.WorkspacePath))
                problems.Add(new AgentDefinitionLaunchProblem("A workspace directory is required to start this agent."));
            else
            {
                try
                {
                    var workspace = Path.GetFullPath(request.Overrides.WorkspacePath);
                    if (!Directory.Exists(workspace))
                        problems.Add(new AgentDefinitionLaunchProblem($"Workspace directory does not exist: {workspace}"));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    problems.Add(new AgentDefinitionLaunchProblem($"Workspace path is invalid: {request.Overrides.WorkspacePath}"));
                }
            }
        }
        if (descriptor.LaunchCapabilities.SupportsSandboxProfile)
        {
            if (request.Overrides.SandboxProfileId is not Guid sandboxProfileId || sandboxProfileId == Guid.Empty)
            {
                problems.Add(new AgentDefinitionLaunchProblem("A sandbox profile is required to start this agent."));
            }
            else
            {
                var profile = await sandboxProfileService.GetByIdAsync(sandboxProfileId);
                if (profile is null)
                {
                    problems.Add(new AgentDefinitionLaunchProblem($"Sandbox profile '{sandboxProfileId}' was not found."));
                }
                else
                {
                    try
                    {
                        var provider = sandboxProviderRegistry.GetRequired(profile.ProviderType);
                        var definition = provider.ParseDefinition(profile.Configuration);
                        var validation = provider.ValidateDefinition(definition);
                        if (!validation.IsValid)
                        {
                            foreach (var error in validation.Errors)
                            {
                                problems.Add(new AgentDefinitionLaunchProblem(error));
                            }
                        }

                        var availability = await provider.CheckAvailabilityAsync(cancellationToken);
                        if (!availability.IsAvailable)
                        {
                            problems.Add(new AgentDefinitionLaunchProblem(
                                availability.ErrorMessage ?? $"Sandbox provider '{profile.ProviderType}' is not available."));
                        }
                    }
                    catch (Exception ex)
                    {
                        problems.Add(new AgentDefinitionLaunchProblem(ex.Message));
                    }
                }
            }
        }
        problems.AddRange(await workflowPreflightValidator.ValidateAsync(reference, cancellationToken));
        ServerModel? model = null;

        if (descriptor.ModelRequirement != AgentModelRequirement.None)
        {
            if (ModelSelectionHelper.TryGetEffectiveModel(
                    descriptor.ConfiguredModel,
                    request.UiModelSelection,
                    out var resolved))
            {
                model = resolved;
            }
            else if (descriptor.ModelRequirement == AgentModelRequirement.Required)
            {
                problems.Add(new AgentDefinitionLaunchProblem("A model selection is required to start this definition."));
            }
        }

        if ((descriptor.LaunchCapabilities.SupportsWorkspace || descriptor.LaunchCapabilities.SupportsSandboxProfile) && model is not null &&
            !await launchCapabilityValidator.SupportsFunctionCallingAsync(model, cancellationToken))
        {
            problems.Add(new AgentDefinitionLaunchProblem(
                descriptor.LaunchCapabilities.SupportsSandboxProfile
                    ? "The selected model does not support function calling required by shell execution."
                    : "The selected model does not support function calling required by File Access."));
        }

        return (new AgentDefinitionLaunchValidation
        {
            CanLaunch = problems.Count == 0,
            Problems = problems
        }, model);
    }

    private static IEnumerable<AgentDefinitionLaunchProblem> ValidateInputs(
        IReadOnlyList<AgentInputDefinition> definitions,
        IReadOnlyDictionary<string, string> values)
    {
        var definitionsByKey = definitions
            .ToDictionary(static input => input.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var key in values.Keys.Where(key => !definitionsByKey.ContainsKey(key)))
            yield return new AgentDefinitionLaunchProblem($"Input '{key}' is not defined.");

        foreach (var input in definitions)
        {
            values.TryGetValue(input.Key, out var value);
            if (input.IsRequired && string.IsNullOrWhiteSpace(value))
                yield return new AgentDefinitionLaunchProblem($"Input '{input.DisplayName}' is required.");
            if (!string.IsNullOrWhiteSpace(value) && input.Kind == AgentInputDefinitionKind.Number &&
                !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                yield return new AgentDefinitionLaunchProblem($"Input '{input.DisplayName}' must be a number.");
            if (!string.IsNullOrWhiteSpace(value) && input.Kind == AgentInputDefinitionKind.Boolean &&
                !bool.TryParse(value, out _))
                yield return new AgentDefinitionLaunchProblem($"Input '{input.DisplayName}' must be true or false.");
            if (!string.IsNullOrWhiteSpace(value) && input.Kind == AgentInputDefinitionKind.Json &&
                !IsValidJson(value))
                yield return new AgentDefinitionLaunchProblem($"Input '{input.DisplayName}' must contain valid JSON.");
        }
    }

    private static bool IsValidJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
