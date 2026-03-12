using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Host;
using ChatClient.Api.PlanningRuntime.Planning;
using ChatClient.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ChatClient.Tests;

public class PlanningRuntimeContractsTests
{
    [Fact]
    public void PlanValidator_RejectsPromptRefsInsideAgentPrompts()
    {
        var plan = new PlanDefinition
        {
            Goal = "Answer the user question.",
            Steps =
            [
                new PlanStep
                {
                    Id = "searchPages",
                    Tool = "search",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("best robot vacuum")
                    }
                },
                new PlanStep
                {
                    Id = "answer",
                    Llm = "synthesizer",
                    SystemPrompt = "Use $searchPages[] to answer the user.",
                    UserPrompt = "Write the final answer.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["pages"] = JsonValue.Create("$searchPages[]")
                    }
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PlanValidator.ValidateOrThrow(plan));

        Assert.Contains("must not embed step refs inside systemPrompt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanningSessionService_StartAsync_RequiresEnabledTools()
    {
        var service = CreateSessionService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(new PlanningRunRequest
        {
            Model = new ServerModel(Guid.NewGuid(), "model-a"),
            UserQuery = "Compare two products",
            EnabledToolNames = []
        }));

        Assert.Equal("At least one planning tool must be enabled.", exception.Message);
    }

    [Fact]
    public void PlanningSessionService_Reset_ClearsProjectedState()
    {
        var service = CreateSessionService();
        service.State.UserQuery = "compare products";
        service.State.IsRunning = true;
        service.State.IsCompleted = true;
        service.State.ActiveStepId = "answer";
        service.State.CurrentPlan = new PlanDefinition
        {
            Goal = "goal",
            Steps =
            [
                new PlanStep
                {
                    Id = "answer",
                    Llm = "synthesizer",
                    SystemPrompt = "sys",
                    UserPrompt = "user",
                    In = []
                }
            ]
        };
        service.State.FinalResult = ChatClient.Api.PlanningRuntime.Common.ResultEnvelope<JsonElement?>.Success(JsonSerializer.SerializeToElement(new { ok = true }));
        service.State.Events.Add(new DiagnosticPlanRunEvent("test", "event"));
        service.State.LogLines.Add("log");
        service.State.AvailableTools.Add(new PlanningToolOption
        {
            Name = "search",
            DisplayName = "Web Search",
            Description = "Search the web"
        });

        service.Reset();

        Assert.Equal(string.Empty, service.State.UserQuery);
        Assert.False(service.State.IsRunning);
        Assert.False(service.State.IsCompleted);
        Assert.Null(service.State.ActiveStepId);
        Assert.Null(service.State.CurrentPlan);
        Assert.Null(service.State.FinalResult);
        Assert.Empty(service.State.Events);
        Assert.Empty(service.State.LogLines);
        Assert.Empty(service.State.AvailableTools);
    }

    private static PlanningSessionService CreateSessionService()
    {
        var chatClientFactory = new Mock<IPlanningChatClientFactory>();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        var searchTool = new WebSearchTool(httpClientFactory.Object, NullLogger<WebSearchTool>.Instance);
        var downloadTool = new WebDownloadTool(httpClientFactory.Object, NullLogger<WebDownloadTool>.Instance);

        return new PlanningSessionService(
            chatClientFactory.Object,
            searchTool,
            downloadTool,
            NullLogger<PlanningSessionService>.Instance);
    }
}
