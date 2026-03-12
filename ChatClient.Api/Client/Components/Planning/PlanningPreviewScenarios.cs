using System.Text.Json;
using System.Text.Json.Nodes;
using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Execution;
using ChatClient.Api.PlanningRuntime.Planning;

namespace ChatClient.Api.Client.Components.Planning;

public sealed class PlanningPreviewScenario
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Description { get; init; }

    public required string UserQuery { get; init; }

    public required PlanDefinition Plan { get; init; }

    public string? ActiveStepId { get; init; }

    public IReadOnlyList<PlanRunEvent> Events { get; init; } = [];

    public IReadOnlyList<string> LogLines { get; init; } = [];

    public ResultEnvelope<JsonElement?>? FinalResult { get; init; }
}

public static class PlanningPreviewScenarios
{
    public const string LiveScenarioId = "live";

    public static IReadOnlyList<PlanningPreviewScenario> All { get; } =
    [
        CreateHappyPathScenario(),
        CreateReplanFailureScenario()
    ];

    public static PlanningPreviewScenario? Get(string? id) =>
        All.FirstOrDefault(scenario => string.Equals(scenario.Id, id, StringComparison.Ordinal));

    private static PlanningPreviewScenario CreateHappyPathScenario()
    {
        var plan = new PlanDefinition
        {
            Goal = "Compare two strong robot vacuum options and produce a short recommendation with trade-offs.",
            Steps =
            [
                new PlanStep
                {
                    Id = "search1",
                    Tool = "search",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("best robot vacuum cleaners for pets and mopping 2026"),
                        ["limit"] = JsonValue.Create(5)
                    },
                    Status = PlanStepStatuses.Done,
                    Result = Element(new[]
                    {
                        new
                        {
                            title = "Best robot vacuum cleaners in 2026",
                            url = "https://www.approachlabs.com/articles/best-robot-vacuum-cleaners-2026-for-pet-hair-and-hardwood-floors",
                            snippet = "Detailed comparison of obstacle avoidance, docking, and self-emptying."
                        },
                        new
                        {
                            title = "Roborock Qrevo Curv review",
                            url = "https://www.techadvisor.com/article/robot-vacuum-roborock-qrevo-curv-review-long-url-for-overflow-checking.html",
                            snippet = "Strong mopping and low-maintenance dock."
                        }
                    })
                },
                new PlanStep
                {
                    Id = "shortlist",
                    Llm = "catalog-filter",
                    SystemPrompt = "You normalize product names and pick two current retail models with enough public specs to compare.",
                    UserPrompt = "From the search results, identify two widely available robot vacuums worth comparing. Return JSON only.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["searchResults"] = Ref("$search1")
                    },
                    Out = "json",
                    Status = PlanStepStatuses.Done,
                    Result = Element(new
                    {
                        models = new[]
                        {
                            "Eufy X10 Pro Omni",
                            "Roborock Qrevo Curv"
                        }
                    })
                },
                new PlanStep
                {
                    Id = "fetch_eufy",
                    Tool = "download",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["url"] = JsonValue.Create("https://support.eufy.com/s/article/X10-Pro-Omni-full-specifications-and-setup-guide"),
                        ["model"] = Ref("$shortlist.models[0]")
                    },
                    Status = PlanStepStatuses.Done,
                    Result = Element(new
                    {
                        suctionPa = 8000,
                        dock = "self-empty + wash + dry",
                        obstacleAvoidance = "AI RGB camera + laser",
                        notes = "Good value positioning and strong pet-hair pickup."
                    })
                },
                new PlanStep
                {
                    Id = "fetch_roborock",
                    Tool = "download",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["url"] = JsonValue.Create("https://us.roborock.com/pages/qrevo-curv-specifications"),
                        ["model"] = Ref("$shortlist.models[1]")
                    },
                    Status = PlanStepStatuses.Done,
                    Result = Element(new
                    {
                        suctionPa = 18500,
                        dock = "self-empty + hot-water wash + drying",
                        obstacleAvoidance = "Reactive AI 3.0",
                        notes = "Premium dock and stronger suction, but more expensive."
                    })
                },
                new PlanStep
                {
                    Id = "compare",
                    Llm = "spec-compare",
                    SystemPrompt = "You compare robot vacuums based only on the supplied structured specs.",
                    UserPrompt = "Summarize the strongest pros, the notable trade-offs, and the best pick for a buyer who wants low-maintenance cleaning.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["candidateA"] = Ref("$fetch_eufy"),
                        ["candidateB"] = Ref("$fetch_roborock")
                    },
                    Out = "json",
                    Status = PlanStepStatuses.Running
                }
            ]
        };

        return new PlanningPreviewScenario
        {
            Id = "happy-path",
            DisplayName = "Mock: Happy Path",
            Description = "Готовый план с несколькими шагами и активным сравнением. Подходит для проверки диаграммы и панели деталей.",
            UserQuery = "Find two popular robot vacuum cleaners, compare their specs, and recommend which one is better.",
            Plan = plan,
            ActiveStepId = "compare",
            Events =
            [
                new StepCallCompletedEvent(
                    "search1",
                    0,
                    true,
                    Element(new[]
                    {
                        "https://www.approachlabs.com/articles/best-robot-vacuum-cleaners-2026-for-pet-hair-and-hardwood-floors",
                        "https://www.techadvisor.com/article/robot-vacuum-roborock-qrevo-curv-review-long-url-for-overflow-checking.html"
                    }),
                    null),
                new AgentPromptPreparedEvent(
                    "shortlist",
                    "catalog-filter",
                    "You normalize product names and pick two current retail models with enough public specs to compare.",
                    "From the search results, identify two widely available robot vacuums worth comparing. Return JSON only.",
                    "Search results:\n- Best robot vacuum cleaners in 2026\n- Roborock Qrevo Curv review\n\nReturn JSON with exactly two model names.",
                    Element(new
                    {
                        searchResults = new[]
                        {
                            "Best robot vacuum cleaners in 2026",
                            "Roborock Qrevo Curv review"
                        }
                    })),
                new AgentResponseReceivedEvent(
                    "shortlist",
                    "catalog-filter",
                    "{\"ok\":true,\"data\":{\"models\":[\"Eufy X10 Pro Omni\",\"Roborock Qrevo Curv\"]},\"error\":null}",
                    true,
                    Element(new
                    {
                        models = new[]
                        {
                            "Eufy X10 Pro Omni",
                            "Roborock Qrevo Curv"
                        }
                    }),
                    null),
                new StepCallCompletedEvent(
                    "fetch_eufy",
                    0,
                    true,
                    Element(new
                    {
                        url = "https://support.eufy.com/s/article/X10-Pro-Omni-full-specifications-and-setup-guide",
                        extracted = new
                        {
                            suctionPa = 8000,
                            dock = "self-empty + wash + dry"
                        }
                    }),
                    null),
                new StepCallCompletedEvent(
                    "fetch_roborock",
                    0,
                    true,
                    Element(new
                    {
                        url = "https://us.roborock.com/pages/qrevo-curv-specifications",
                        extracted = new
                        {
                            suctionPa = 18500,
                            dock = "self-empty + hot-water wash + drying"
                        }
                    }),
                    null),
                new AgentPromptPreparedEvent(
                    "compare",
                    "spec-compare",
                    "You compare robot vacuums based only on the supplied structured specs.",
                    "Summarize the strongest pros, the notable trade-offs, and the best pick for a buyer who wants low-maintenance cleaning.",
                    "Candidate A: Eufy X10 Pro Omni\nCandidate B: Roborock Qrevo Curv\n\nFocus on maintenance burden, mopping quality, and value.",
                    Element(new
                    {
                        candidateA = new
                        {
                            model = "Eufy X10 Pro Omni",
                            suctionPa = 8000
                        },
                        candidateB = new
                        {
                            model = "Roborock Qrevo Curv",
                            suctionPa = 18500
                        }
                    }))
            ],
            LogLines =
            [
                "[orchestrator] attempt=1 phase=plan [plan] create:start toolCount=2 query=Find two popular robot vacuum cleaners, compare their specs, and recommend which one is better.",
                "[exec] step=search1 success=True calls=1 outputUrls=2",
                "[exec] step=shortlist success=True models=[\"Eufy X10 Pro Omni\",\"Roborock Qrevo Curv\"]",
                "[exec] step=fetch_eufy success=True url=https://support.eufy.com/s/article/X10-Pro-Omni-full-specifications-and-setup-guide",
                "[exec] step=fetch_roborock success=True url=https://us.roborock.com/pages/qrevo-curv-specifications",
                "[exec] step=compare running=True awaiting_llm=True"
            ]
        };
    }

    private static PlanningPreviewScenario CreateReplanFailureScenario()
    {
        var plan = new PlanDefinition
        {
            Goal = "Compare multiple robot vacuums, but demonstrate the UI for a replan loop with ambiguous extraction and long diagnostic text.",
            Steps =
            [
                new PlanStep
                {
                    Id = "search1",
                    Tool = "search",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["query"] = JsonValue.Create("best robot vacuum cleaners 2024 detailed comparison pet hair mop dock"),
                        ["limit"] = JsonValue.Create(5)
                    },
                    Status = PlanStepStatuses.Done,
                    Result = Element(new[]
                    {
                        "https://www.approachlabs.com/the-best-robot-vacuum-cleaners-in-2024/343d6bdb29b07f0c4a8888fd98d4fa5e",
                        "https://www.techadvisor.com/article/724333/best-robot-vacuum-cleaners-2024-review-roundup.html",
                        "https://www.telegraph.co.uk/recommended/home/cleaning/best-robot-vacuum-cleaners-tried-tested/"
                    })
                },
                new PlanStep
                {
                    Id = "extract1",
                    Llm = "spec-extractor",
                    SystemPrompt = "Extract a single product spec sheet from the supplied article text.",
                    UserPrompt = "Read the article and extract one robot vacuum specification object only.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["articleUrls"] = Ref("$search1")
                    },
                    Out = "json",
                    Status = PlanStepStatuses.Fail,
                    Error = new PlanStepError
                    {
                        Code = "multiple_models",
                        Message = "Execution has failed steps.",
                        Details = Element(new
                        {
                            missing = new[]
                            {
                                "extract1"
                            },
                            attempt = 1
                        })
                    }
                },
                new PlanStep
                {
                    Id = "replan_models",
                    Llm = "replanner",
                    SystemPrompt = "Split ambiguous extraction into one step per model candidate and keep all dependencies explicit.",
                    UserPrompt = "Generate a safer plan after the extractor failed because the page described multiple products.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["failedStep"] = Ref("$extract1.err"),
                        ["searchResults"] = Ref("$search1")
                    },
                    Out = "json",
                    Status = PlanStepStatuses.Done,
                    Result = Element(new
                    {
                        replacementSteps = new[]
                        {
                            "extract2",
                            "verify"
                        }
                    })
                },
                new PlanStep
                {
                    Id = "extract2",
                    Llm = "spec-extractor",
                    SystemPrompt = "Extract a single product spec sheet from the supplied article text.",
                    UserPrompt = "Extract one specific vacuum model only. If the page contains multiple models, fail with a precise blocking reason.",
                    In = new Dictionary<string, JsonNode?>
                    {
                        ["articleUrls"] = Ref("$search1"),
                        ["replanHints"] = Ref("$replan_models")
                    },
                    Out = "json",
                    Status = PlanStepStatuses.Fail,
                    Error = new PlanStepError
                    {
                        Code = "multiple_models",
                        Message = "Multiple robot vacuum models are described in the input; unable to extract a single specification object.",
                        Details = Element(new
                        {
                            missing = new[]
                            {
                                "extract2"
                            },
                            attempt = 3
                        })
                    }
                }
            ]
        };

        return new PlanningPreviewScenario
        {
            Id = "replan-failure",
            DisplayName = "Mock: Replan Failure",
            Description = "Сценарий с длинными URL, replan-раундами и финальной ошибкой. Нужен для проверки переносов и стабильности графа.",
            UserQuery = "Compare several robot vacuum cleaners and explain why the current plan keeps failing.",
            Plan = plan,
            ActiveStepId = "extract2",
            Events =
            [
                new StepCallCompletedEvent(
                    "search1",
                    0,
                    true,
                    Element(new[]
                    {
                        "https://www.approachlabs.com/the-best-robot-vacuum-cleaners-in-2024/343d6bdb29b07f0c4a8888fd98d4fa5e",
                        "https://www.orenteatai.com/blog/the-best-vacuum-robots-of-2024-your-ultimate-cleaning-companion/343d6bdb29b07f0c4a8888fd98d4fa5e",
                        "https://www.techadvisor.com/article/724333/best-robot-vacuum-cleaners-2024-review-roundup.html"
                    }),
                    null),
                new AgentPromptPreparedEvent(
                    "extract2",
                    "spec-extractor",
                    "Extract a single product spec sheet from the supplied article text.",
                    "Extract one specific vacuum model only. If the page contains multiple models, fail with a precise blocking reason.",
                    "The source contains multiple models: Eufy X10 Pro Omni, Samsung Bespoke Jet Bot Combo AI+, Beko VRR61414VB RoboSmart, and others.\nReturn a single spec object or fail with a blocking error.",
                    Element(new
                    {
                        urls = new[]
                        {
                            "https://www.approachlabs.com/the-best-robot-vacuum-cleaners-in-2024/343d6bdb29b07f0c4a8888fd98d4fa5e",
                            "https://www.orenteatai.com/blog/the-best-vacuum-robots-of-2024-your-ultimate-cleaning-companion/343d6bdb29b07f0c4a8888fd98d4fa5e"
                        }
                    })),
                new AgentResponseReceivedEvent(
                    "extract2",
                    "spec-extractor",
                    "{\"ok\":false,\"error\":{\"code\":\"multiple_models\",\"message\":\"Multiple robot vacuum models are described in the input; unable to extract a single specification object.\"}}",
                    false,
                    null,
                    new ErrorInfo(
                        "multiple_models",
                        "Multiple robot vacuum models are described in the input; unable to extract a single specification object.",
                        Element(new
                        {
                            details = new[]
                            {
                                "multiple models (Eufy X10 Pro Omni, Samsung Bespoke Jet Bot Combo AI+, Beko VRR61414VB RoboSmart, etc.)"
                            }
                        }))),
                new ReplanRoundCompletedEvent(
                    1,
                    false,
                    "Need to split extraction into model-specific steps.",
                    Element(new
                    {
                        action = "replace_step",
                        target = "extract1",
                        with = new[]
                        {
                            "replan_models",
                            "extract2"
                        }
                    }),
                    Element(new
                    {
                        applied = true,
                        validation = "ok"
                    })),
                new ReplanRoundCompletedEvent(
                    2,
                    true,
                    "Execution has failed steps.",
                    Element(new
                    {
                        action = "stop",
                        reason = "Repeated ambiguity after replan"
                    }),
                    Element(new
                    {
                        finalVerdict = "failed",
                        missing = new[]
                        {
                            "extract2"
                        }
                    }))
            ],
            LogLines =
            [
                "[exec] step=extract1 success=False calls=1 error=Multiple robot vacuum models are described in the input; unable to extract a single specification object. details={\"status\":\"blocked\",\"needsReplan\":true,\"type\":\"missing\",\"details\":[\"multiple models (Eufy X10 Pro Omni, Samsung Bespoke Jet Bot Combo AI+, Beko VRR61414VB RoboSmart, etc.)\"]}",
                "[verify] goal:action=Replan reason=Execution has failed steps.",
                "[replan] round=1 applied=True action={\"action\":\"replace_step\",\"target\":\"extract1\",\"with\":[\"replan_models\",\"extract2\"]}",
                "[exec] step=extract2 success=False calls=1 error=Multiple robot vacuum models are described in the input; unable to extract a single specification object. details={\"status\":\"blocked\",\"needsReplan\":true,\"type\":\"missing\",\"details\":[\"multiple models (Eufy X10 Pro Omni, Samsung Bespoke Jet Bot Combo AI+, Beko VRR61414VB RoboSmart, etc.)\"]}",
                "[verify] goal:action=Replan reason=Execution has failed steps.",
                "[planning] final_error={\"code\":\"multiple_models\",\"message\":\"Execution has failed steps.\",\"details\":{\"missing\":[\"extract2\"],\"attempt\":3}}"
            ],
            FinalResult = ResultEnvelope<JsonElement?>.Failure(
                "multiple_models",
                "Execution has failed steps.",
                Element(new
                {
                    missing = new[]
                    {
                        "extract2"
                    },
                    attempt = 3
                }))
        };
    }

    private static JsonNode? Ref(string value) => JsonValue.Create(value);

    private static JsonElement Element<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
