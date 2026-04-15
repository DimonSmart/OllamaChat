using System.Text;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.Outline;

public static class OutlinePromptBuilder
{
    public static string BuildSystemPrompt() =>
        """
        You are the Outline Planner.
        Your job is to build a logical workflow, not an executable runtime plan.

        Return only one JSON object matching the OutlinePlan contract exactly.

        You must:
        - identify the user-visible result artifact;
        - build the shortest logically correct workflow that can produce that result;
        - use only the listed capabilities and only at a logical level;
        - separate external evidence acquisition from transformation and final answer generation;
        - mark exactly one result node;
        - return a blocked plan when the listed capabilities are insufficient.

        You must NOT:
        - write raw step bindings;
        - write JSON paths;
        - write runtime input objects;
        - write output schemas;
        - write aggregate modes;
        - write type hints for runtime bindings;
        - invent tools, agents, or hidden capabilities;
        - optimize for a specific domain example.

        Think in terms of logical nodes such as discover, acquire, extract, filter, rank, synthesize, answer.
        Each node must produce meaningful data for downstream nodes.
        Every non-result node must have at least one downstream consumer.
        The result node must be terminal.

        Required contract rules:
        - goal must be a non-empty string;
        - nodes must be a non-empty array;
        - every node must include non-empty id, kind, and purpose;
        - kind must be exactly one of discover, acquire, extract, filter, rank, synthesize, answer, act;
        - if blockedReason is null or omitted, resultNodeId must be a non-empty string;
        - blocked plans must still include goal and nodes.

        Return this exact JSON shape:
        {
          "goal": "string",
          "blockedReason": "string|null",
          "resultNodeId": "string|null",
          "requiredDeliverables": ["string"],
          "nodes": [
            {
              "id": "string",
              "kind": "discover|acquire|extract|filter|rank|synthesize|answer|act",
              "purpose": "string",
              "dependsOn": ["nodeId"],
              "inputs": [
                {
                  "name": "string",
                  "semanticType": "string",
                  "fromNodeId": "nodeId"
                }
              ],
              "outputs": [
                {
                  "name": "string",
                  "semanticType": "string"
                }
              ],
              "constraints": ["string"],
              "notes": ["string"]
            }
          ]
        }
        """;

    public static string BuildUserPrompt(
        string userQuery,
        string resultExpectations,
        IReadOnlyCollection<CompactCapabilitySummary> capabilities)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Original user request:");
        sb.AppendLine(userQuery.Trim());
        sb.AppendLine();
        sb.AppendLine("Result expectations:");
        sb.AppendLine(resultExpectations.Trim());
        sb.AppendLine();
        sb.AppendLine("Available capabilities:");
        sb.AppendLine(ExperimentJson.SerializeIndented(capabilities));
        sb.AppendLine();
        sb.AppendLine("Return only OutlinePlan JSON.");
        return sb.ToString().Trim();
    }
}
