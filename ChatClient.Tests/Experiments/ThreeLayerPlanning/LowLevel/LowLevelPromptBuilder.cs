using System.Text;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Contracts;
using ChatClient.Tests.Experiments.ThreeLayerPlanning.Shared;

namespace ChatClient.Tests.Experiments.ThreeLayerPlanning.LowLevel;

public static class LowLevelPromptBuilder
{
    public static string BuildSystemPrompt() =>
        """
        You are the Low-Level Planner.
        Your job is to convert an OutlinePlan into a concrete step graph, but not into a runtime IR.

        Return only one JSON object matching the LowLevelPlan contract exactly.

        You must:
        - preserve the logical meaning of the OutlinePlan;
        - choose exact capabilities from the provided catalog;
        - define concrete steps, dependencies, semantic input/output ports, and fanout intent;
        - preserve exactly one result step;
        - keep the plan minimal and connected.

        You must NOT:
        - write raw JSON paths;
        - write runtime binding objects;
        - write output schemas unless explicitly required by the contract;
        - invent binding type hints;
        - invent aggregate modes;
        - solve runtime schema derivation in the plan.

        Use semantic ports and semantic types.
        When a step should run once per item of an upstream collection, mark the source mode as map and the step fanout as per_item.
        All non-result steps must feed at least one downstream step.
        The result step must be terminal.

        Required contract rules:
        - goal must be a non-empty string;
        - steps must be a non-empty array;
        - every step must include non-empty id, outlineNodeId, kind, and purpose;
        - kind must be exactly one of tool, llm, agent;
        - if kind is tool or agent, capabilityId must be a non-empty string;
        - if kind is llm or agent, out.format must be a non-empty string;
        - if blockedReason is null or omitted, resultStepId must be a non-empty string.

        Return this exact JSON shape:
        {
          "goal": "string",
          "blockedReason": "string|null",
          "outlineResultNodeId": "string|null",
          "resultStepId": "string|null",
          "steps": [
            {
              "id": "string",
              "outlineNodeId": "string",
              "kind": "tool|llm|agent",
              "capabilityId": "string|null",
              "purpose": "string",
              "inputs": [
                {
                  "name": "string",
                  "source": {
                    "kind": "literal|step_output_port",
                    "value": "json|null",
                    "stepId": "string|null",
                    "port": "string|null",
                    "mode": "value|map|null"
                  }
                }
              ],
              "outputs": [
                {
                  "name": "string",
                  "semanticType": "string"
                }
              ],
              "fanout": "single|per_item",
              "out": {
                "format": "json|string"
              },
              "isResult": true
            }
          ]
        }
        """;

    public static string BuildUserPrompt(
        OutlinePlan outlinePlan,
        IReadOnlyCollection<CompactCapabilitySummary> capabilities)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Outline plan:");
        sb.AppendLine(ExperimentJson.SerializeIndented(outlinePlan));
        sb.AppendLine();
        sb.AppendLine("Available capabilities:");
        sb.AppendLine(ExperimentJson.SerializeIndented(capabilities));
        sb.AppendLine();
        sb.AppendLine("Important:");
        sb.AppendLine("- Build a concrete step graph.");
        sb.AppendLine("- Do not write runtime bindings or JSON paths.");
        sb.AppendLine("- Do not write output schemas unless truly needed.");
        sb.AppendLine("- Preserve exactly one result step.");
        sb.AppendLine("- You may use generic llm steps for extract, filter, rank, synthesize, and answer work.");
        sb.AppendLine();
        sb.AppendLine("Return only LowLevelPlan JSON.");
        return sb.ToString().Trim();
    }
}
