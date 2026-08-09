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
            TodoProviderProfileId = TodoProviderProfileId,
            AgentModeProviderProfileId = AgentModeProviderProfileId,
            FileAccessProviderProfileId = FileAccessProviderProfileId,
            SkillsProviderProfileId = SkillsProviderProfileId,
            CompactionProfileId = CompactionProfileId,
            RagProviderProfileId = RagProviderProfileId,
            EnableShell = EnableShell,
            ContinueUntilTodosComplete = ContinueUntilTodosComplete,
            MaxTodoCompletionIterations = MaxTodoCompletionIterations,
            RuntimeAgentId = RuntimeAgentId,
            McpServerBindings = McpServerBindings
                .Select(static binding => binding.Clone())
                .ToList(),
            KnowledgeStoreIds = KnowledgeStoreIds.ToList(),
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };
    }
}
