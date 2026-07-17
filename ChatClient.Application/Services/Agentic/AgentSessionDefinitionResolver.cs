using ChatClient.Application.Helpers;
using ChatClient.Application.Services;
using ChatClient.Application.Services.AgentRuntime;
using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public sealed record AgentSessionDefinitionRequest
{
    public ServerModelSelection UiModelSelection { get; init; } = new(null, null);

    public IReadOnlyDictionary<string, string> Inputs { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
    Task<ResolvedAgentSessionDefinition> ResolveAsync(
        AgentDefinitionReference reference,
        AgentSessionDefinitionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class AgentSessionDefinitionResolver(
    IAgentDefinitionCatalog catalog,
    IAgentTemplateService agentTemplateService) : IAgentSessionDefinitionResolver
{
    public async Task<ResolvedAgentSessionDefinition> ResolveAsync(
        AgentDefinitionReference reference,
        AgentSessionDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = await catalog.GetRequiredAsync(reference, cancellationToken);
        var problems = ValidateInputs(descriptor.Inputs, request.Inputs).ToList();
        ServerModel? model = null;

        if (descriptor.ModelRequirement != AgentModelRequirement.None)
        {
            var configured = new ServerModelSelection(null, null);
            if (reference.Kind == AgentDefinitionKind.SavedAgent && Guid.TryParse(reference.Id, out var agentId))
            {
                var agent = (await agentTemplateService.GetAllAsync())
                    .FirstOrDefault(candidate => candidate.Id == agentId);
                if (agent is not null)
                    configured = new ServerModelSelection(agent.LlmId, agent.ModelName);
            }

            if (ModelSelectionHelper.TryGetEffectiveModel(configured, request.UiModelSelection, out var resolved))
                model = resolved;
            else if (descriptor.ModelRequirement == AgentModelRequirement.Required)
                problems.Add(new AgentDefinitionLaunchProblem("A model selection is required to start this definition."));
        }

        var validation = new AgentDefinitionLaunchValidation
        {
            CanLaunch = problems.Count == 0,
            Problems = problems
        };

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

    private static IEnumerable<AgentDefinitionLaunchProblem> ValidateInputs(
        IReadOnlyList<AgentInputDefinition> definitions,
        IReadOnlyDictionary<string, string> values)
    {
        foreach (var input in definitions)
        {
            values.TryGetValue(input.Key, out var value);
            if (input.IsRequired && input.Kind != AgentInputDefinitionKind.Boolean && string.IsNullOrWhiteSpace(value))
                yield return new AgentDefinitionLaunchProblem($"Input '{input.DisplayName}' is required.");
            if (!string.IsNullOrWhiteSpace(value) && input.Kind == AgentInputDefinitionKind.Number &&
                !decimal.TryParse(value, out _))
                yield return new AgentDefinitionLaunchProblem($"Input '{input.DisplayName}' must be a number.");
            if (!string.IsNullOrWhiteSpace(value) && input.Kind == AgentInputDefinitionKind.Boolean &&
                !bool.TryParse(value, out _))
                yield return new AgentDefinitionLaunchProblem($"Input '{input.DisplayName}' must be true or false.");
        }
    }
}
