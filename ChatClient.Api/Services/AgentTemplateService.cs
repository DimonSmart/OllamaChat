using ChatClient.Application.Repositories;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;

namespace ChatClient.Api.Services;

public class AgentTemplateService(IAgentTemplateRepository repository) : IAgentTemplateService
{
    private readonly IAgentTemplateRepository _repository = repository;

    public async Task<IReadOnlyCollection<AgentTemplateDefinition>> GetAllAsync()
    {
        return await LoadTemplatesAsync();
    }

    public async Task<AgentTemplateDefinition?> GetByIdAsync(Guid templateId)
    {
        var templates = await LoadTemplatesAsync();
        return templates.FirstOrDefault(template => template.Id == templateId);
    }

    public async Task CreateAsync(AgentTemplateDefinition template)
    {
        var templates = await LoadTemplatesAsync();
        EnsureBindingIds(template);
        var usedIds = templates
            .Select(static item => item.Id)
            .Where(static id => id != Guid.Empty)
            .ToHashSet();

        if (template.Id == Guid.Empty || !usedIds.Add(template.Id))
        {
            template.Id = GenerateUniqueId(usedIds);
        }

        var now = DateTime.UtcNow;
        template.CreatedAt = now;
        template.UpdatedAt = now;
        templates.Add(template);
        await _repository.SaveAllAsync(templates);
    }

    public async Task UpdateAsync(AgentTemplateDefinition template)
    {
        var templates = await LoadTemplatesAsync();
        EnsureBindingIds(template);
        var index = templates.FindIndex(item => item.Id == template.Id);
        if (index == -1)
            throw new KeyNotFoundException($"Agent template with ID {template.Id} not found");
        template.UpdatedAt = DateTime.UtcNow;
        templates[index] = template;
        await _repository.SaveAllAsync(templates);
    }

    public async Task DeleteAsync(Guid templateId)
    {
        var templates = await LoadTemplatesAsync();
        var existing = templates.FirstOrDefault(item => item.Id == templateId) ??
                       throw new KeyNotFoundException($"Agent template with ID {templateId} not found");
        templates.Remove(existing);
        await _repository.SaveAllAsync(templates);
    }

    private async Task<List<AgentTemplateDefinition>> LoadTemplatesAsync() =>
        (await _repository.GetAllAsync()).ToList();

    private static bool EnsureBindingIds(AgentTemplateDefinition template)
    {
        var hasChanges = false;

        foreach (var binding in template.McpServerBindings)
        {
            if (binding.BindingId is Guid bindingId && bindingId != Guid.Empty)
            {
                continue;
            }

            binding.BindingId = Guid.NewGuid();
            hasChanges = true;
        }

        return hasChanges;
    }

    private static Guid GenerateUniqueId(HashSet<Guid> usedIds)
    {
        Guid id;
        do
        {
            id = Guid.NewGuid();
        }
        while (!usedIds.Add(id));

        return id;
    }
}

