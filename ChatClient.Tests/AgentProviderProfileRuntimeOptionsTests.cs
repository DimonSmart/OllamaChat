using ChatClient.Api.Client.Services.Agentic;
using ChatClient.Application.Services.Agentic;
using ChatClient.Domain.Models;
using Microsoft.Agents.AI;

namespace ChatClient.Tests;

public class AgentProviderProfileRuntimeOptionsTests
{
    [Fact]
    public void BuildTodoProviderOptions_MapsProfileAndRendersTemplate()
    {
        var options = AgenticRuntimeAgentFactory.BuildTodoProviderOptions(new TodoProviderProfile
        {
            Instructions = " Use a concise plan. ",
            SuppressTodoListMessage = true,
            TodoListMessageTemplate = "Current work:\n{todos}"
        });

        Assert.Equal("Use a concise plan.", options.Instructions);
        Assert.True(options.SuppressTodoListMessage);
        var message = options.TodoListMessageBuilder!([
            new TodoItem { Title = "Plan", Description = "Outline the work", IsComplete = true },
            new TodoItem { Title = "Implement" }
        ]);
        Assert.Equal($"Current work:\n- [x] Plan: Outline the work{Environment.NewLine}- [ ] Implement", message);
    }

    [Fact]
    public void BuildAgentModeProviderOptions_MapsInstructionsModesAndDefault()
    {
        var options = AgenticRuntimeAgentFactory.BuildAgentModeProviderOptions(new AgentModeProviderProfile
        {
            Instructions = "Modes: {available_modes}; current: {current_mode}",
            DefaultMode = "execute",
            Modes =
            [
                new AgentModeProfile { Name = "plan", Instructions = "Plan with the user." },
                new AgentModeProfile { Name = "execute", Instructions = "Perform approved work." }
            ]
        });

        Assert.Equal("Modes: {available_modes}; current: {current_mode}", options.Instructions);
        Assert.Equal("execute", options.DefaultMode);
        Assert.Collection(
            options.Modes!,
            mode => Assert.Equal(("plan", "Plan with the user."), (mode.Name, mode.Instructions)),
            mode => Assert.Equal(("execute", "Perform approved work."), (mode.Name, mode.Instructions)));
    }

    [Fact]
    public void BuildContextProviders_AddsConfiguredTodoProviderWithoutDefaultProvider()
    {
        var request = new AgentRunRequest
        {
            Agent = new AgentExecutionSpec { Id = Guid.NewGuid() },
            ResolvedModel = new ServerModel(Guid.NewGuid(), "test-model"),
            Configuration = new AppChatConfiguration("test-model", []),
            Conversation = [],
            UserMessage = "Hello"
        };

        var providers = AgenticRuntimeAgentFactory.BuildContextProviders(
            request,
            null!,
            new TodoProviderProfile { Instructions = "Track the work." });

        Assert.Equal(2, providers.Count);
        Assert.IsType<TodoProvider>(providers[1]);
    }

    [Fact]
    public void AgentExecutionSpecFactory_PreservesProviderSelections()
    {
        var todoProfileId = Guid.NewGuid();
        var modeProfileId = Guid.NewGuid();
        var spec = AgentExecutionSpecFactory.FromTemplate(new AgentTemplateDefinition
        {
            TodoProviderProfileId = todoProfileId,
            AgentModeProviderProfileId = modeProfileId
        });

        Assert.Equal(todoProfileId, spec.TodoProviderProfileId);
        Assert.Equal(modeProfileId, spec.AgentModeProviderProfileId);
    }
}
