using ChatClient.Api.PlanningRuntime.LowLevel;
using ChatClient.Api.PlanningRuntime.Shared;
using ChatClient.Api.Services;
using System.Text;
using System.Text.Json.Nodes;

namespace ChatClient.Api.PlanningRuntime.Runtime;

internal static class RuntimeLlmPromptInputLimiter
{
    private const string MarkerPrefix = "[web-content-truncated";

    public static RuntimeLlmPromptPreparation Prepare(
        JsonObject inputs,
        IReadOnlyDictionary<string, ResolvedRuntimeInput> resolvedInputs,
        IReadOnlyDictionary<string, RuntimeStep> stepsById,
        IReadOnlyDictionary<string, AppToolDescriptor> toolsById,
        RuntimeLlmPromptingOptions options,
        bool retryAttempt)
    {
        var preparedInputs = (JsonObject)inputs.DeepClone();
        var truncatedInputNames = new List<string>();
        var truncatedInputNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var budget = retryAttempt
            ? new ContentBudget(options.RetryContentChars, options.RetryHeadChars, options.RetryTailChars)
            : new ContentBudget(options.NormalContentChars, options.NormalHeadChars, options.NormalTailChars);
        var truncatedValueCount = 0;
        var canRetryWithMoreAggressiveTruncation = false;

        foreach (var input in resolvedInputs)
        {
            if (!preparedInputs.TryGetPropertyValue(input.Key, out var inputNode)
                || inputNode is null
                || !IsBuiltInWebDownloadInput(input.Value.SourceStepId, stepsById, toolsById))
            {
                continue;
            }

            var inputWasTruncated = false;
            var inputCanRetry = false;
            TruncateContentValues(
                inputNode,
                budget,
                options.RetryContentChars,
                retryAttempt,
                ref truncatedValueCount,
                ref inputWasTruncated,
                ref inputCanRetry);

            if (inputWasTruncated && truncatedInputNameSet.Add(input.Key))
                truncatedInputNames.Add(input.Key);

            canRetryWithMoreAggressiveTruncation |= inputCanRetry;
        }

        var inputsChars = preparedInputs.ToJsonString(PlanningNodeJson.SerializerOptions).Length;
        return new RuntimeLlmPromptPreparation(
            preparedInputs,
            inputsChars,
            truncatedValueCount > 0,
            truncatedValueCount,
            truncatedInputNames,
            canRetryWithMoreAggressiveTruncation);
    }

    private static bool IsBuiltInWebDownloadInput(
        string? sourceStepId,
        IReadOnlyDictionary<string, RuntimeStep> stepsById,
        IReadOnlyDictionary<string, AppToolDescriptor> toolsById)
    {
        if (string.IsNullOrWhiteSpace(sourceStepId)
            || !stepsById.TryGetValue(sourceStepId, out var sourceStep)
            || !string.Equals(sourceStep.Kind, LowLevelStepKinds.Tool, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        toolsById.TryGetValue(sourceStep.CapabilityId ?? string.Empty, out var tool);
        return RuntimeToolCapabilityMatcher.IsBuiltInWebDownload(tool, sourceStep.CapabilityId);
    }

    private static void TruncateContentValues(
        JsonNode node,
        ContentBudget budget,
        int retryContentChars,
        bool retryAttempt,
        ref int truncatedValueCount,
        ref bool wasTruncated,
        ref bool canRetryWithMoreAggressiveTruncation,
        string? propertyName = null)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    if (property.Value is null)
                        continue;

                    TruncateContentValues(
                        property.Value,
                        budget,
                        retryContentChars,
                        retryAttempt,
                        ref truncatedValueCount,
                        ref wasTruncated,
                        ref canRetryWithMoreAggressiveTruncation,
                        property.Key);
                }
                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is null)
                        continue;

                    TruncateContentValues(
                        item,
                        budget,
                        retryContentChars,
                        retryAttempt,
                        ref truncatedValueCount,
                        ref wasTruncated,
                        ref canRetryWithMoreAggressiveTruncation,
                        propertyName);
                }
                break;

            case JsonValue value when string.Equals(propertyName, "content", StringComparison.OrdinalIgnoreCase)
                && value.TryGetValue<string>(out var text):
                if (!retryAttempt && text.Length > retryContentChars)
                    canRetryWithMoreAggressiveTruncation = true;

                if (text.Length <= budget.MaxContentChars)
                    return;

                ReplaceValue(value, BuildTruncatedContent(text, budget));
                truncatedValueCount++;
                wasTruncated = true;
                break;
        }
    }

    private static string BuildTruncatedContent(string text, ContentBudget budget)
    {
        var omittedLength = Math.Max(0, text.Length - budget.HeadChars - budget.TailChars);
        var marker = new StringBuilder()
            .AppendLine()
            .Append(MarkerPrefix)
            .Append(' ')
            .Append("originalLength=").Append(text.Length).Append(' ')
            .Append("keptHead=").Append(budget.HeadChars).Append(' ')
            .Append("keptTail=").Append(budget.TailChars).Append(' ')
            .Append("omittedLength=").Append(omittedLength)
            .Append(']')
            .AppendLine()
            .ToString();

        return string.Concat(
            text.AsSpan(0, budget.HeadChars),
            marker,
            text.AsSpan(text.Length - budget.TailChars, budget.TailChars));
    }

    private static void ReplaceValue(JsonValue value, string replacement)
    {
        var parent = value.Parent;
        switch (parent)
        {
            case JsonObject obj:
                var key = obj.First(pair => ReferenceEquals(pair.Value, value)).Key;
                obj[key] = replacement;
                break;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    if (!ReferenceEquals(array[index], value))
                        continue;

                    array[index] = replacement;
                    return;
                }
                break;
        }
    }

    private sealed record ContentBudget(int MaxContentChars, int HeadChars, int TailChars);
}

internal sealed record RuntimeLlmPromptPreparation(
    JsonObject PreparedInputs,
    int InputsChars,
    bool TruncationAttempted,
    int TruncatedValueCount,
    IReadOnlyList<string> TruncatedInputNames,
    bool CanRetryWithMoreAggressiveTruncation);

internal sealed record ResolvedRuntimeInput(
    JsonNode? Value,
    string Mode,
    string? SourceStepId,
    string? SourcePort);
