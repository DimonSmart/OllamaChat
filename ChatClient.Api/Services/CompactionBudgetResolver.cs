using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public sealed class CompactionBudgetResolver(IModelRuntimeLimitsService runtimeLimits) : ICompactionBudgetResolver
{
    public async Task<CompactionBudget> ResolveAsync(CompactionProfile profile, ServerModel model)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(model);

        var limits = profile.BudgetSource switch
        {
            CompactionBudgetSources.Fixed => new ModelRuntimeLimits
            {
                ContextWindowTokens = profile.ContextWindowTokens ?? 0,
                MaxOutputTokens = profile.MaxOutputTokens ?? 0
            },
            CompactionBudgetSources.SelectedModel => await runtimeLimits.GetAsync(model.ServerId, model.ModelName)
                ?? throw new InvalidOperationException($"Compaction profile '{profile.Name}' requires runtime limits for model '{model.ModelName}' on server {model.ServerId}. Configure the model limits before starting the agent."),
            _ => throw new InvalidOperationException($"Compaction profile '{profile.Name}' has an invalid budget source.")
        };

        if (!ModelRuntimeLimitValidation.HasValidTokenBudget(limits.ContextWindowTokens, limits.MaxOutputTokens))
            throw new InvalidOperationException($"Compaction profile '{profile.Name}' has invalid limits for model '{model.ModelName}' on server {model.ServerId}. Context window must exceed maximum output.");

        return new CompactionBudget(limits.ContextWindowTokens, limits.MaxOutputTokens, limits.ContextWindowTokens - limits.MaxOutputTokens);
    }
}
