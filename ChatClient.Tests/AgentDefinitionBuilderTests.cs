using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Extensions.AI;

namespace ChatClient.Tests;

public class AgentDefinitionBuilderTests
{
    [Fact]
    public void NewAgent_BuildsDefinitionWithExecutionAndBindings()
    {
        var serverId = Guid.NewGuid();

        var definition = AgentDefinitionBuilder
            .New("Character Reader", "character-reader")
            .WithInstructions("Read the cursor and update the registry.")
            .WithDefaultModel(serverId, "model-a")
            .WithTemperature(0.2)
            .WithRepeatPenalty(1.1)
            .AutoSelectTools(6)
            .ConfigureExecution(execution => execution
                .WithMaxToolCalls(12)
                .UseToolWindowCompaction(4, "cursor_next", "save_registry"))
            .WithBinding("character-registry", binding => binding
                .DisplayAs("Registry")
                .OnlyTools("read_registry", "save_registry")
                .WithRoots(@"C:\books")
                .WithParameter("registryId", "characters"))
            .Build();

        Assert.Equal("Character Reader", definition.AgentName);
        Assert.Equal("character-reader", definition.ShortName);
        Assert.Equal("Read the cursor and update the registry.", definition.Content);
        Assert.Equal(serverId, definition.LlmId);
        Assert.Equal("model-a", definition.ModelName);
        Assert.Equal(0.2, definition.Temperature);
        Assert.Equal(1.1, definition.RepeatPenalty);
        Assert.Equal(6, definition.FunctionSettings.AutoSelectCount);
        Assert.Equal(12, definition.ExecutionSettings.MaxToolCalls);
        Assert.True(definition.ExecutionSettings.HistoryCompaction.Enabled);
        Assert.Equal(AgentHistoryCompactionModes.ToolWindow, definition.ExecutionSettings.HistoryCompaction.Mode);
        Assert.Equal(4, definition.ExecutionSettings.HistoryCompaction.KeepLastToolPairs);
        Assert.Equal(["cursor_next", "save_registry"], definition.ExecutionSettings.HistoryCompaction.ToolNames);

        var binding = Assert.Single(definition.McpServerBindings);
        Assert.Equal("character-registry", binding.ServerName);
        Assert.Equal("Registry", binding.DisplayName);
        Assert.False(binding.SelectAllTools);
        Assert.Equal(["read_registry", "save_registry"], binding.SelectedTools);
        Assert.Equal([@"C:\books"], binding.Roots);
        Assert.Equal("characters", binding.Parameters["registryId"]);
    }

    [Fact]
    public void FromExistingAgent_CanOverrideDefinitionAndBuildRunRequest()
    {
        var saved = new AgentDescription
        {
            Id = Guid.NewGuid(),
            AgentName = "Knowledge Reader",
            ShortName = "knowledge-reader",
            Content = "Read the knowledge base.",
            ModelName = "model-old",
            LlmId = Guid.NewGuid(),
            McpServerBindings =
            [
                new McpServerSessionBinding
                {
                    BindingId = Guid.NewGuid(),
                    ServerName = "knowledge-book",
                    Enabled = true,
                    SelectAllTools = true
                }
            ]
        };
        var resolvedModel = new ServerModel(Guid.NewGuid(), "model-new");

        var request = AgentDefinitionBuilder
            .From(saved)
            .WithInstructions("Read only chapter 1 and return structured notes.")
            .WithBinding("knowledge-book", binding => binding
                .OnlyTools("kb_search_sections")
                .WithParameter("knowledgeFile", @"C:\kb\book.json"))
            .Build()
            .ForRun()
            .UsingModel(resolvedModel)
            .WithFunctions("mock-web:search")
            .AddMessage(ChatRole.System, "Existing conversation")
            .WithUserMessage("Scan chapter 1")
            .Build();

        Assert.Equal("Knowledge Reader", request.Agent.AgentName);
        Assert.Equal("Read only chapter 1 and return structured notes.", request.Agent.Content);
        Assert.Equal(resolvedModel.ServerId, request.Agent.LlmId);
        Assert.Equal(resolvedModel.ModelName, request.Agent.ModelName);
        Assert.Equal(resolvedModel.ServerId, request.ResolvedModel.ServerId);
        Assert.Equal(resolvedModel.ModelName, request.Configuration.ModelName);
        Assert.Equal(["mock-web:search"], request.Configuration.Functions);
        Assert.Single(request.Conversation);
        Assert.Equal(ChatRole.System, request.Conversation[0].Role);
        Assert.Equal("Existing conversation", request.Conversation[0].Text);
        Assert.Equal("Scan chapter 1", request.UserMessage);

        var binding = Assert.Single(request.Agent.McpServerBindings);
        Assert.Equal("knowledge-book", binding.ServerName);
        Assert.False(binding.SelectAllTools);
        Assert.Equal(["kb_search_sections"], binding.SelectedTools);
        Assert.Equal(@"C:\kb\book.json", binding.Parameters["knowledgeFile"]);
    }

    [Fact]
    public void ForRun_CanUseDefinitionDefaultModel()
    {
        var serverId = Guid.NewGuid();
        var definition = AgentDefinitionBuilder
            .New("Planner", "planner")
            .WithDefaultModel(serverId, "model-a")
            .Build();

        var request = definition
            .ForRun()
            .UsingDefaultModel()
            .WithUserMessage("Plan the task.")
            .Build();

        Assert.Equal(serverId, request.ResolvedModel.ServerId);
        Assert.Equal("model-a", request.ResolvedModel.ModelName);
        Assert.Equal("model-a", request.Configuration.ModelName);
        Assert.Equal("Plan the task.", request.UserMessage);
    }
}
