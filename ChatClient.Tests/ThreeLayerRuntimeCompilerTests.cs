using ChatClient.Api.PlanningRuntime.LowLevel;
using ChatClient.Api.PlanningRuntime.Outline;
using ChatClient.Api.PlanningRuntime.Runtime;
using ChatClient.Api.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChatClient.Tests;

public sealed class ThreeLayerRuntimeCompilerTests
{
    [Fact]
    public void LowLevelValidator_RejectsAcquireNodeMaterializedAsSearch()
    {
        var outline = CreateOutlinePlan();
        var plan = new LowLevelPlan
        {
            Goal = outline.Goal,
            OutlineResultNodeId = "n_answer",
            ResultStepId = "s3",
            Steps =
            [
                CreateSearchStep("s1", "n_discover", literalQuery: "robot vacuum"),
                CreateSearchStep(
                    "s2",
                    "n_second",
                    sourceStepId: "s1",
                    sourcePort: "results",
                    sourceMode: LowLevelInputModes.Map),
                CreateAnswerStep("s3", "n_answer", "s2", "results")
            ]
        };

        var validation = LowLevelValidator.Validate(plan, outline, CreateTools());

        var issue = Assert.Single(validation.Issues, issue => issue.Code == "outline_execution_contract_mismatch");
        Assert.Contains("acquire", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LowLevelValidator_AndCompiler_RejectSearchObjectsBoundIntoSearchQuery_AndSuggestDownload()
    {
        var outline = CreateOutlinePlan(
            discoverKind: OutlineNodeKinds.Discover,
            secondNodeKind: OutlineNodeKinds.Discover);
        var plan = new LowLevelPlan
        {
            Goal = outline.Goal,
            OutlineResultNodeId = "n_answer",
            ResultStepId = "s3",
            Steps =
            [
                CreateSearchStep("s1", "n_discover", literalQuery: "popular robot vacuum"),
                CreateSearchStep(
                    "s2",
                    "n_second",
                    sourceStepId: "s1",
                    sourcePort: "results",
                    sourceMode: LowLevelInputModes.Map),
                CreateAnswerStep("s3", "n_answer", "s2", "results")
            ]
        };

        var tools = CreateTools();
        var validation = LowLevelValidator.Validate(plan, outline, tools);
        var compilerResult = new RuntimePlannerCompiler(tools).Compile(plan);

        var validationIssue = Assert.Single(validation.Issues, issue => issue.Code == "binding_tool_schema_mismatch");
        var compileIssue = Assert.Single(compilerResult.Issues, issue => issue.Code == "binding_tool_schema_mismatch");

        Assert.False(validation.IsValid);
        Assert.False(compilerResult.IsSuccess);

        using var validationDetails = JsonDocument.Parse(validationIssue.Details!.Value.GetRawText());
        Assert.Equal("s2", validationDetails.RootElement.GetProperty("stepId").GetString());
        Assert.Equal("query", validationDetails.RootElement.GetProperty("inputName").GetString());
        Assert.Equal("s1", validationDetails.RootElement.GetProperty("sourceStepId").GetString());
        Assert.Equal("results", validationDetails.RootElement.GetProperty("sourcePort").GetString());
        Assert.Equal("string", validationDetails.RootElement.GetProperty("expectedSchema").GetString());
        Assert.Equal("object", validationDetails.RootElement.GetProperty("actualSchema").GetString());
        Assert.Equal("page", validationDetails.RootElement.GetProperty("suggestedInputName").GetString());
        Assert.Equal("map", validationDetails.RootElement.GetProperty("suggestedBindingMode").GetString());
        Assert.Contains(
            validationDetails.RootElement.GetProperty("suggestedCapabilityIds").EnumerateArray().Select(static item => item.GetString()),
            static capabilityId => string.Equals(capabilityId, "binding:11111111111111111111111111111111:download", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("query", compileIssue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LowLevelValidator_AndCompiler_AcceptDiscoverSearch_ThenAcquireDownload()
    {
        var outline = CreateOutlinePlan();
        var plan = new LowLevelPlan
        {
            Goal = outline.Goal,
            OutlineResultNodeId = "n_answer",
            ResultStepId = "s3",
            Steps =
            [
                CreateSearchStep("s1", "n_discover", literalQuery: "best robot vacuum under 600"),
                CreateDownloadStep("s2", "n_second", "s1", "results", LowLevelInputModes.Map),
                CreateAnswerStep("s3", "n_answer", "s2", "documents")
            ]
        };

        var tools = CreateTools();
        var validation = LowLevelValidator.Validate(plan, outline, tools);
        var compilerResult = new RuntimePlannerCompiler(tools).Compile(plan);

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
        Assert.True(compilerResult.IsSuccess, string.Join(Environment.NewLine, compilerResult.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));

        Assert.NotNull(compilerResult.Plan);
        var runtimePlan = compilerResult.Plan!;
        var acquireStep = Assert.Single(runtimePlan.Steps, step => step.Id == "s2");
        Assert.Equal(LowLevelInputModes.Map, acquireStep.In["page"].Mode);
        Assert.Equal("document[]", acquireStep.Outputs.Single().SemanticType);
    }

    private static OutlinePlan CreateOutlinePlan(
        string discoverKind = OutlineNodeKinds.Discover,
        string secondNodeKind = OutlineNodeKinds.Acquire) =>
        new()
        {
            Goal = "Find two popular robot vacuum cleaners, compare their specs, and recommend which one is better.",
            ResultNodeId = "n_answer",
            Nodes =
            [
                new OutlineNode
                {
                    Id = "n_discover",
                    Kind = discoverKind,
                    Purpose = "Find candidate robot vacuum pages.",
                    Outputs = [new OutlineNodeOutput { Name = "references", SemanticType = "reference[]" }]
                },
                new OutlineNode
                {
                    Id = "n_second",
                    Kind = secondNodeKind,
                    Purpose = "Acquire candidate robot vacuum documents.",
                    DependsOn = ["n_discover"],
                    Inputs = [new OutlineNodeInput { Name = "references", SemanticType = "reference[]", FromNodeId = "n_discover" }],
                    Outputs = [new OutlineNodeOutput { Name = "documents", SemanticType = "document[]" }]
                },
                new OutlineNode
                {
                    Id = "n_answer",
                    Kind = OutlineNodeKinds.Answer,
                    Purpose = "Write the final recommendation.",
                    DependsOn = ["n_second"],
                    Inputs = [new OutlineNodeInput { Name = "documents", SemanticType = "document[]", FromNodeId = "n_second" }],
                    Outputs = [new OutlineNodeOutput { Name = "answer", SemanticType = "answer" }]
                }
            ]
        };

    private static LowLevelStep CreateSearchStep(
        string stepId,
        string outlineNodeId,
        string? literalQuery = null,
        string? sourceStepId = null,
        string? sourcePort = null,
        string? sourceMode = null) =>
        new()
        {
            Id = stepId,
            OutlineNodeId = outlineNodeId,
            Kind = LowLevelStepKinds.Tool,
            CapabilityId = "binding:11111111111111111111111111111111:search",
            Purpose = "Search for robot vacuum candidates.",
            Inputs =
            [
                literalQuery is not null
                    ? new LowLevelStepInput
                    {
                        Name = "query",
                        Source = new LowLevelInputSource
                        {
                            Kind = LowLevelInputSourceKinds.Literal,
                            Value = JsonValue.Create(literalQuery)
                        }
                    }
                    : new LowLevelStepInput
                    {
                        Name = "query",
                        Source = new LowLevelInputSource
                        {
                            Kind = LowLevelInputSourceKinds.StepOutputPort,
                            StepId = sourceStepId,
                            Port = sourcePort,
                            Mode = sourceMode
                        }
                    }
            ],
            Outputs =
            [
                new LowLevelStepOutput
                {
                    Name = "results",
                    SemanticType = "reference[]"
                }
            ],
            Fanout = LowLevelFanoutModes.Single
        };

    private static LowLevelStep CreateDownloadStep(
        string stepId,
        string outlineNodeId,
        string sourceStepId,
        string sourcePort,
        string sourceMode) =>
        new()
        {
            Id = stepId,
            OutlineNodeId = outlineNodeId,
            Kind = LowLevelStepKinds.Tool,
            CapabilityId = "binding:11111111111111111111111111111111:download",
            Purpose = "Download candidate pages.",
            Inputs =
            [
                new LowLevelStepInput
                {
                    Name = "page",
                    Source = new LowLevelInputSource
                    {
                        Kind = LowLevelInputSourceKinds.StepOutputPort,
                        StepId = sourceStepId,
                        Port = sourcePort,
                        Mode = sourceMode
                    }
                }
            ],
            Outputs =
            [
                new LowLevelStepOutput
                {
                    Name = "documents",
                    SemanticType = "document"
                }
            ],
            Fanout = LowLevelFanoutModes.PerItem
        };

    private static LowLevelStep CreateAnswerStep(
        string stepId,
        string outlineNodeId,
        string sourceStepId,
        string sourcePort) =>
        new()
        {
            Id = stepId,
            OutlineNodeId = outlineNodeId,
            Kind = LowLevelStepKinds.Llm,
            Purpose = "Write the final answer.",
            Inputs =
            [
                new LowLevelStepInput
                {
                    Name = "documents",
                    Source = new LowLevelInputSource
                    {
                        Kind = LowLevelInputSourceKinds.StepOutputPort,
                        StepId = sourceStepId,
                        Port = sourcePort,
                        Mode = LowLevelInputModes.Value
                    }
                }
            ],
            Outputs =
            [
                new LowLevelStepOutput
                {
                    Name = "answer",
                    SemanticType = "answer"
                }
            ],
            Fanout = LowLevelFanoutModes.Single,
            Out = new LowLevelStepOutputSettings
            {
                Format = RuntimeOutputFormats.String
            },
            IsResult = true
        };

    private static IReadOnlyCollection<AppToolDescriptor> CreateTools() =>
        [
            CreateSearchDescriptor(),
            CreateDownloadDescriptor()
        ];

    private static AppToolDescriptor CreateSearchDescriptor() =>
        new(
            QualifiedName: "binding:11111111111111111111111111111111:search",
            ServerName: "Built-in Web MCP Server",
            ToolName: "search",
            DisplayName: "search",
            Description: "Search for candidate pages.",
            InputSchema: ParseJson(
                """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string" },
                    "limit": { "type": "integer" }
                  },
                  "required": ["query"]
                }
                """),
            OutputSchema: ParseJson(
                """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string" },
                    "results": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "url": { "type": "string" },
                          "title": { "type": "string" },
                          "provider": { "type": "string" }
                        },
                        "required": ["url", "title", "provider"]
                      }
                    }
                  },
                  "required": ["query", "results"]
                }
                """),
            MayRequireUserInput: false,
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            OpenWorldHint: true,
            ExecuteAsync: static (_, _) => Task.FromResult<object>(new object()),
            BaseQualifiedName: "built-in-web:search",
            BaseServerName: "built-in-web",
            PlanningMetadata: new AppToolPlanningMetadata(
                Purpose: "Find candidate page references.",
                PlannerRole: AppToolPlannerRole.Discover,
                ProducesKind: AppToolProducesKind.Reference));

    private static AppToolDescriptor CreateDownloadDescriptor() =>
        new(
            QualifiedName: "binding:11111111111111111111111111111111:download",
            ServerName: "Built-in Web MCP Server",
            ToolName: "download",
            DisplayName: "download",
            Description: "Download a single page from a page reference.",
            InputSchema: ParseJson(
                """
                {
                  "type": "object",
                  "properties": {
                    "page": {
                      "type": "object",
                      "properties": {
                        "url": { "type": "string" },
                        "title": { "type": ["string", "null"] },
                        "provider": { "type": ["string", "null"] }
                      },
                      "required": ["url"]
                    },
                    "url": { "type": "string" }
                  },
                  "oneOf": [
                    { "required": ["page"] },
                    { "required": ["url"] }
                  ]
                }
                """),
            OutputSchema: ParseJson(
                """
                {
                  "type": "object",
                  "properties": {
                    "url": { "type": "string" },
                    "title": { "type": "string" },
                    "content": { "type": "string" }
                  },
                  "required": ["url", "title", "content"]
                }
                """),
            MayRequireUserInput: false,
            ReadOnlyHint: true,
            DestructiveHint: false,
            IdempotentHint: true,
            OpenWorldHint: true,
            ExecuteAsync: static (_, _) => Task.FromResult<object>(new object()),
            BaseQualifiedName: "built-in-web:download",
            BaseServerName: "built-in-web",
            PlanningMetadata: new AppToolPlanningMetadata(
                Purpose: "Download full page content from a reference.",
                PlannerRole: AppToolPlannerRole.Acquire,
                ProducesKind: AppToolProducesKind.Document));

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
