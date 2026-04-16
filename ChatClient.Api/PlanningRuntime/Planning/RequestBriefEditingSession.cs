using ChatClient.Api.PlanningRuntime.Common;
using System.Text.Json.Nodes;

namespace ChatClient.Api.PlanningRuntime.Planning;

internal sealed class RequestBriefEditingSession
{
    private string _rewrittenRequest = string.Empty;
    private string _goal = string.Empty;
    private string _expectedResult = string.Empty;
    private string _outputExpectations = string.Empty;
    private readonly List<string> _deliverables = [];
    private readonly List<string> _constraints = [];
    private readonly List<string> _acquisitionNeeds = [];
    private readonly List<string> _evidenceRequirements = [];
    private readonly List<string> _reasoningNeeds = [];
    private readonly List<string> _successCriteria = [];
    private readonly List<string> _ambiguityNotes = [];
    private readonly List<string> _suggestedPlanOutline = [];

    public JsonObject GetCurrentBriefJson() => new()
    {
        ["rewrittenRequest"] = _rewrittenRequest,
        ["goal"] = _goal,
        ["expectedResult"] = _expectedResult,
        ["deliverables"] = CreateArray(_deliverables),
        ["constraints"] = CreateArray(_constraints),
        ["acquisitionNeeds"] = CreateArray(_acquisitionNeeds),
        ["evidenceRequirements"] = CreateArray(_evidenceRequirements),
        ["reasoningNeeds"] = CreateArray(_reasoningNeeds),
        ["successCriteria"] = CreateArray(_successCriteria),
        ["ambiguityNotes"] = CreateArray(_ambiguityNotes),
        ["outputExpectations"] = _outputExpectations,
        ["suggestedPlanOutline"] = CreateArray(_suggestedPlanOutline)
    };

    public RequestBrief BuildBrief() => new()
    {
        RewrittenRequest = _rewrittenRequest,
        Goal = _goal,
        ExpectedResult = _expectedResult,
        Deliverables = [.. _deliverables],
        Constraints = [.. _constraints],
        AcquisitionNeeds = [.. _acquisitionNeeds],
        EvidenceRequirements = [.. _evidenceRequirements],
        ReasoningNeeds = [.. _reasoningNeeds],
        SuccessCriteria = [.. _successCriteria],
        AmbiguityNotes = [.. _ambiguityNotes],
        OutputExpectations = _outputExpectations,
        SuggestedPlanOutline = [.. _suggestedPlanOutline]
    };

    public JsonObject ExecuteAction(string toolName, JsonObject input)
    {
        try
        {
            return toolName switch
            {
                "brief.read" => CreateSuccess(toolName, GetCurrentBriefJson()),
                "brief.setScalar" => CreateSuccess(
                    toolName,
                    SetScalar(GetRequiredString(input, "fieldName"), GetOptionalString(input, "value"))),
                "brief.appendListItem" => CreateSuccess(
                    toolName,
                    AppendListItem(GetRequiredString(input, "listName"), GetRequiredString(input, "item"))),
                "brief.replaceList" => CreateSuccess(
                    toolName,
                    ReplaceList(GetRequiredString(input, "listName"), input["items"])),
                "brief.validate" => Validate(),
                _ => CreateFailure("unknown_tool", $"Unknown request-brief tool '{toolName ?? "<null>"}'.", toolName)
            };
        }
        catch (Exception ex)
        {
            return CreateFailure("tool_error", ex.Message, toolName);
        }
    }

    private JsonObject SetScalar(string fieldName, string? value)
    {
        var normalized = NormalizeScalar(value);
        switch (fieldName)
        {
            case "rewrittenRequest":
                _rewrittenRequest = normalized;
                break;
            case "goal":
                _goal = normalized;
                break;
            case "expectedResult":
                _expectedResult = normalized;
                break;
            case "outputExpectations":
                _outputExpectations = normalized;
                break;
            default:
                throw new InvalidOperationException($"Unknown scalar field '{fieldName}'.");
        }

        return new JsonObject
        {
            ["fieldName"] = fieldName,
            ["value"] = normalized
        };
    }

    private JsonObject AppendListItem(string listName, string item)
    {
        var normalized = NormalizeListItem(item);
        var target = ResolveList(listName);
        if (!target.Contains(normalized, StringComparer.Ordinal))
            target.Add(normalized);

        return new JsonObject
        {
            ["listName"] = listName,
            ["count"] = target.Count
        };
    }

    private JsonObject ReplaceList(string listName, JsonNode? itemsNode)
    {
        var target = ResolveList(listName);
        var items = DeserializeStringArray(itemsNode);
        target.Clear();
        foreach (var item in items)
        {
            if (!target.Contains(item, StringComparer.Ordinal))
                target.Add(item);
        }

        return new JsonObject
        {
            ["listName"] = listName,
            ["count"] = target.Count
        };
    }

    private JsonObject Validate()
    {
        try
        {
            BuildBrief().ValidateOrThrow();
            return new JsonObject
            {
                ["tool"] = "brief.validate",
                ["ok"] = true
            };
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["tool"] = "brief.validate",
                ["ok"] = false,
                ["error"] = new JsonObject
                {
                    ["code"] = "invalid_brief",
                    ["message"] = ex.Message
                }
            };
        }
    }

    private List<string> ResolveList(string listName) =>
        listName switch
        {
            "deliverables" => _deliverables,
            "constraints" => _constraints,
            "acquisitionNeeds" => _acquisitionNeeds,
            "evidenceRequirements" => _evidenceRequirements,
            "reasoningNeeds" => _reasoningNeeds,
            "successCriteria" => _successCriteria,
            "ambiguityNotes" => _ambiguityNotes,
            "suggestedPlanOutline" => _suggestedPlanOutline,
            _ => throw new InvalidOperationException($"Unknown list field '{listName}'.")
        };

    private static string GetRequiredString(JsonObject input, string propertyName)
    {
        var value = GetOptionalString(input, propertyName);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        throw new InvalidOperationException($"Action input '{propertyName}' is required.");
    }

    private static string? GetOptionalString(JsonObject input, string propertyName) =>
        input[propertyName]?.GetValue<string>()?.Trim();

    private static string NormalizeScalar(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeListItem(string value)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("List item must not be blank.");

        return normalized;
    }

    private static List<string> DeserializeStringArray(JsonNode? itemsNode)
    {
        var array = itemsNode as JsonArray
            ?? throw new InvalidOperationException("Action input 'items' must be an array of strings.");

        var items = new List<string>(array.Count);
        foreach (var itemNode in array)
        {
            if (itemNode is null)
                throw new InvalidOperationException("Action input 'items' must not contain null.");

            var value = itemNode.GetValue<string>();
            items.Add(NormalizeListItem(value));
        }

        return items;
    }

    private static JsonArray CreateArray(IEnumerable<string> values) =>
        new(values.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static JsonObject CreateSuccess(string? toolName, JsonNode? output) => new()
    {
        ["tool"] = toolName,
        ["ok"] = true,
        ["output"] = output?.DeepClone()
    };

    private static JsonObject CreateFailure(string code, string message, string? toolName = null) => new()
    {
        ["tool"] = toolName,
        ["ok"] = false,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    };
}
