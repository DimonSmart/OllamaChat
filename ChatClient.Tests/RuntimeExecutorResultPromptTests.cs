using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.LowLevel;
using ChatClient.Api.PlanningRuntime.Runtime;
using ChatClient.Api.PlanningRuntime.Shared;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatClient.Tests;

public sealed class RuntimeExecutorResultPromptTests
{
    [Fact]
    public async Task RuntimePlanExecutor_InstructsJsonResultStepToUseUserFacingAnswer()
    {
        var llmClient = new PromptCapturingPlanningLlmClient(
            ResultEnvelope<JsonElement?>.Success(
                JsonSerializer.SerializeToElement(new
                {
                    userFacingAnswer = "# Final answer\n\n- first\n- second",
                    sources = new[] { "doc-1", "doc-2" }
                })));
        var executor = new RuntimePlanExecutor(llmClient, []);

        var result = await executor.ExecuteAsync(new RuntimePlan
        {
            Goal = "Return the final answer.",
            ResultStepId = "final_answer",
            ResultPort = "result",
            Steps =
            [
                new RuntimeStep
                {
                    Id = "final_answer",
                    Kind = LowLevelStepKinds.Llm,
                    Purpose = "Write the final user-facing answer.",
                    Outputs =
                    [
                        new RuntimeStepOutput
                        {
                            Name = "result",
                            SemanticType = "answer"
                        }
                    ],
                    Out = new RuntimeStepOutputSettings
                    {
                        Format = RuntimeOutputFormats.Json
                    },
                    IsResult = true
                }
            ]
        });

        Assert.True(result.Succeeded);
        var finalObject = Assert.IsType<JsonObject>(result.FinalOutput);
        Assert.Equal("# Final answer\n\n- first\n- second", finalObject["userFacingAnswer"]!.GetValue<string>());
        Assert.Single(llmClient.SystemPrompts);
        Assert.Single(llmClient.UserPrompts);
        Assert.Contains("userFacingAnswer", llmClient.SystemPrompts[0], StringComparison.Ordinal);
        Assert.Contains("Result step rules:", llmClient.UserPrompts[0], StringComparison.Ordinal);
        Assert.Contains("data.userFacingAnswer", llmClient.UserPrompts[0], StringComparison.Ordinal);
    }

    private sealed class PromptCapturingPlanningLlmClient(ResultEnvelope<JsonElement?> response) : IPlanningLlmClient
    {
        public List<string> SystemPrompts { get; } = [];

        public List<string> UserPrompts { get; } = [];

        public Task<ResultEnvelope<JsonElement?>> GenerateEnvelopeAsync(
            string agentName,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default)
        {
            SystemPrompts.Add(systemPrompt);
            UserPrompts.Add(userPrompt);
            return Task.FromResult(response);
        }
    }
}
