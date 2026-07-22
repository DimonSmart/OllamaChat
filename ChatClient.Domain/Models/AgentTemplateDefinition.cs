namespace ChatClient.Domain.Models;

public sealed class AgentTemplateDefinition : AgentModelBase
{
    public AgentTemplateDefinition Clone()
    {
        return new AgentTemplateDefinition
        {
            Id = Id,
            AgentName = AgentName,
            Summary = Summary,
            Content = Content,
            ShortName = ShortName,
            AvatarText = AvatarText,
            ModelName = ModelName,
            LlmId = LlmId,
            Temperature = Temperature,
            RepeatPenalty = RepeatPenalty,
            RuntimeAgentId = RuntimeAgentId,
            FunctionSettings = new FunctionSettings
            {
                AutoSelectCount = FunctionSettings.AutoSelectCount
            },
            McpServerBindings = McpServerBindings
                .Select(static binding => binding.Clone())
                .ToList(),
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };
    }
}
