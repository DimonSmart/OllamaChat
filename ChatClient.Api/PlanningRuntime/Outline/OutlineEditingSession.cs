using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Shared;
using System.Text.Json.Nodes;

namespace ChatClient.Api.PlanningRuntime.Outline;

internal sealed class OutlineEditingSession
{
    private string _goal = string.Empty;
    private string? _blockedReason;
    private string? _resultNodeId;
    private readonly List<string> _requiredDeliverables = [];
    private readonly List<OutlineNode> _nodes = [];

    public JsonObject GetCurrentPlanJson() =>
        PlanningNodeJson.ToNode(BuildPlan())?.AsObject()
        ?? new JsonObject();

    public OutlinePlan BuildPlan() => new()
    {
        Goal = _goal,
        BlockedReason = NormalizeOptional(_blockedReason),
        ResultNodeId = NormalizeOptional(_resultNodeId),
        RequiredDeliverables = [.. _requiredDeliverables],
        Nodes = [.. _nodes.Select(CloneNode)]
    };

    public JsonObject ExecuteAction(string toolName, JsonObject input)
    {
        try
        {
            return toolName switch
            {
                "outline.readPlan" => CreateSuccess(toolName, GetCurrentPlanJson()),
                "outline.setGoal" => CreateSuccess(toolName, SetGoal(GetRequiredString(input, "goal"))),
                "outline.setBlockedReason" => CreateSuccess(toolName, SetBlockedReason(GetOptionalString(input, "blockedReason"))),
                "outline.appendRequiredDeliverable" => CreateSuccess(toolName, AppendRequiredDeliverable(GetRequiredString(input, "deliverable"))),
                "outline.replaceRequiredDeliverables" => CreateSuccess(toolName, ReplaceRequiredDeliverables(input["deliverables"])),
                "outline.addNode" => CreateSuccess(toolName, AddNode(GetOptionalString(input, "afterNodeId"), input["node"])),
                "outline.replaceNode" => CreateSuccess(toolName, ReplaceNode(GetRequiredString(input, "nodeId"), input["node"])),
                "outline.removeNode" => CreateSuccess(toolName, RemoveNode(GetRequiredString(input, "nodeId"))),
                "outline.linkNodes" => CreateSuccess(
                    toolName,
                    LinkNodes(
                        GetRequiredString(input, "fromNodeId"),
                        GetRequiredString(input, "toNodeId"),
                        GetRequiredString(input, "inputName"),
                        GetRequiredString(input, "semanticType"))),
                "outline.markResultNode" => CreateSuccess(toolName, MarkResultNode(GetRequiredString(input, "nodeId"))),
                "outline.validate" => Validate(),
                _ => CreateFailure("unknown_tool", $"Unknown outline tool '{toolName ?? "<null>"}'.", toolName)
            };
        }
        catch (Exception ex)
        {
            return CreateFailure("tool_error", ex.Message, toolName);
        }
    }

    private JsonObject SetGoal(string goal)
    {
        _goal = goal.Trim();
        return new JsonObject
        {
            ["goal"] = _goal
        };
    }

    private JsonObject SetBlockedReason(string? blockedReason)
    {
        _blockedReason = NormalizeOptional(blockedReason);
        if (!string.IsNullOrWhiteSpace(_blockedReason))
            _resultNodeId = null;

        return new JsonObject
        {
            ["blockedReason"] = _blockedReason is null ? null : JsonValue.Create(_blockedReason)
        };
    }

    private JsonObject AppendRequiredDeliverable(string deliverable)
    {
        var normalized = deliverable.Trim();
        if (!_requiredDeliverables.Contains(normalized, StringComparer.Ordinal))
            _requiredDeliverables.Add(normalized);

        return new JsonObject
        {
            ["count"] = _requiredDeliverables.Count
        };
    }

    private JsonObject ReplaceRequiredDeliverables(JsonNode? deliverablesNode)
    {
        var deliverables = DeserializeStringArray(deliverablesNode, "deliverables");
        _requiredDeliverables.Clear();
        foreach (var deliverable in deliverables)
        {
            if (!_requiredDeliverables.Contains(deliverable, StringComparer.Ordinal))
                _requiredDeliverables.Add(deliverable);
        }

        return new JsonObject
        {
            ["count"] = _requiredDeliverables.Count
        };
    }

    private JsonObject AddNode(string? afterNodeId, JsonNode? nodeNode)
    {
        var node = DeserializeNode(nodeNode);
        EnsureUniqueNodeId(node.Id, excludedIndex: null);

        var insertIndex = string.IsNullOrWhiteSpace(afterNodeId)
            ? _nodes.Count
            : FindNodeIndex(afterNodeId) + 1;
        _nodes.Insert(insertIndex, node);

        return new JsonObject
        {
            ["nodeId"] = node.Id,
            ["position"] = insertIndex
        };
    }

    private JsonObject ReplaceNode(string nodeId, JsonNode? nodeNode)
    {
        var index = FindNodeIndex(nodeId);
        var node = DeserializeNode(nodeNode);
        EnsureUniqueNodeId(node.Id, index);
        _nodes[index] = node;

        if (string.Equals(_resultNodeId, nodeId, StringComparison.OrdinalIgnoreCase))
            _resultNodeId = node.Id;

        foreach (var candidate in _nodes)
        {
            for (var i = 0; i < candidate.DependsOn.Count; i++)
            {
                if (string.Equals(candidate.DependsOn[i], nodeId, StringComparison.OrdinalIgnoreCase))
                    candidate.DependsOn[i] = node.Id;
            }

            foreach (var input in candidate.Inputs.Where(input => string.Equals(input.FromNodeId, nodeId, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                candidate.Inputs.Remove(input);
                candidate.Inputs.Add(new OutlineNodeInput
                {
                    Name = input.Name,
                    SemanticType = input.SemanticType,
                    FromNodeId = node.Id
                });
            }
        }

        return new JsonObject
        {
            ["nodeId"] = node.Id,
            ["position"] = index
        };
    }

    private JsonObject RemoveNode(string nodeId)
    {
        var index = FindNodeIndex(nodeId);
        _nodes.RemoveAt(index);

        foreach (var node in _nodes)
        {
            node.DependsOn.RemoveAll(dependsOn => string.Equals(dependsOn, nodeId, StringComparison.OrdinalIgnoreCase));
            node.Inputs.RemoveAll(input => string.Equals(input.FromNodeId, nodeId, StringComparison.OrdinalIgnoreCase));
        }

        if (string.Equals(_resultNodeId, nodeId, StringComparison.OrdinalIgnoreCase))
            _resultNodeId = null;

        return new JsonObject
        {
            ["nodeId"] = nodeId,
            ["remainingCount"] = _nodes.Count
        };
    }

    private JsonObject LinkNodes(string fromNodeId, string toNodeId, string inputName, string semanticType)
    {
        FindNodeIndex(fromNodeId);
        var toNode = _nodes[FindNodeIndex(toNodeId)];

        if (!toNode.DependsOn.Contains(fromNodeId, StringComparer.OrdinalIgnoreCase))
            toNode.DependsOn.Add(fromNodeId);

        toNode.Inputs.RemoveAll(input => string.Equals(input.Name, inputName, StringComparison.OrdinalIgnoreCase));
        toNode.Inputs.Add(new OutlineNodeInput
        {
            Name = inputName,
            SemanticType = semanticType,
            FromNodeId = fromNodeId
        });

        return new JsonObject
        {
            ["fromNodeId"] = fromNodeId,
            ["toNodeId"] = toNodeId,
            ["inputName"] = inputName
        };
    }

    private JsonObject MarkResultNode(string nodeId)
    {
        FindNodeIndex(nodeId);
        _resultNodeId = nodeId;
        _blockedReason = null;

        return new JsonObject
        {
            ["resultNodeId"] = _resultNodeId
        };
    }

    private JsonObject Validate()
    {
        var plan = BuildPlan();
        var validation = OutlineValidator.Validate(plan);
        if (validation.IsValid)
        {
            return new JsonObject
            {
                ["tool"] = "outline.validate",
                ["ok"] = true
            };
        }

        return new JsonObject
        {
            ["tool"] = "outline.validate",
            ["ok"] = false,
            ["error"] = new JsonObject
            {
                ["code"] = "invalid_outline",
                ["message"] = validation.Issues[0].Message,
                ["details"] = PlanningNodeJson.ToNode(validation.Issues)
            }
        };
    }

    private int FindNodeIndex(string nodeId)
    {
        var index = _nodes.FindIndex(node => string.Equals(node.Id, nodeId, StringComparison.OrdinalIgnoreCase));
        return index >= 0
            ? index
            : throw new InvalidOperationException($"Outline node '{nodeId}' was not found.");
    }

    private void EnsureUniqueNodeId(string nodeId, int? excludedIndex)
    {
        var duplicateIndex = _nodes.FindIndex(node => string.Equals(node.Id, nodeId, StringComparison.OrdinalIgnoreCase));
        if (duplicateIndex >= 0 && duplicateIndex != excludedIndex)
            throw new InvalidOperationException($"Outline node id '{nodeId}' already exists.");
    }

    private static OutlineNode DeserializeNode(JsonNode? nodeNode)
    {
        var node = nodeNode is null
            ? throw new InvalidOperationException("Action input 'node' must be a valid outline node object.")
            : PlanningNodeJson.DeserializeNode<OutlineNode>(nodeNode);

        if (string.IsNullOrWhiteSpace(node.Id))
            throw new InvalidOperationException("Outline node id is required.");
        if (string.IsNullOrWhiteSpace(node.Kind))
            throw new InvalidOperationException("Outline node kind is required.");
        if (string.IsNullOrWhiteSpace(node.Purpose))
            throw new InvalidOperationException("Outline node purpose is required.");

        return CloneNode(node);
    }

    private static List<string> DeserializeStringArray(JsonNode? node, string propertyName)
    {
        var array = node as JsonArray
            ?? throw new InvalidOperationException($"Action input '{propertyName}' must be an array of strings.");
        return array.Select(item =>
            {
                var value = item?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(value))
                    throw new InvalidOperationException($"Action input '{propertyName}' must not contain blank values.");

                return value.Trim();
            })
            .ToList();
    }

    private static string GetRequiredString(JsonObject input, string propertyName)
    {
        var value = GetOptionalString(input, propertyName);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        throw new InvalidOperationException($"Action input '{propertyName}' is required.");
    }

    private static string? GetOptionalString(JsonObject input, string propertyName) =>
        input[propertyName]?.GetValue<string>()?.Trim();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static OutlineNode CloneNode(OutlineNode node) => new()
    {
        Id = node.Id.Trim(),
        Kind = node.Kind.Trim(),
        Purpose = node.Purpose.Trim(),
        DependsOn = [.. node.DependsOn.Select(static value => value.Trim()).Where(static value => !string.IsNullOrWhiteSpace(value))],
        Inputs = [.. node.Inputs.Select(static input => new OutlineNodeInput
        {
            Name = input.Name.Trim(),
            SemanticType = input.SemanticType.Trim(),
            FromNodeId = input.FromNodeId.Trim()
        })],
        Outputs = [.. node.Outputs.Select(static output => new OutlineNodeOutput
        {
            Name = output.Name.Trim(),
            SemanticType = output.SemanticType.Trim()
        })],
        Constraints = [.. node.Constraints.Select(static value => value.Trim()).Where(static value => !string.IsNullOrWhiteSpace(value))],
        Notes = [.. node.Notes.Select(static value => value.Trim()).Where(static value => !string.IsNullOrWhiteSpace(value))]
    };

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
