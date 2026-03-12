using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatClient.Api.PlanningRuntime.Common;

namespace ChatClient.Api.PlanningRuntime.Verification;

public interface IFinalAnswerVerifier
{
    Task<FinalAnswerVerificationResult> VerifyAsync(
        string userQuery,
        JsonElement? answer,
        CancellationToken cancellationToken = default);
}

public sealed record FinalAnswerVerificationResult
{
    public bool IsAnswer { get; init; }

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyCollection<string> Missing { get; init; } = [];
}

public sealed class LlmFinalAnswerVerifier(IChatClient chatClient) : IFinalAnswerVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<FinalAnswerVerificationResult> VerifyAsync(
        string userQuery,
        JsonElement? answer,
        CancellationToken cancellationToken = default)
    {
        if (answer is null)
        {
            return new FinalAnswerVerificationResult
            {
                IsAnswer = false,
                Reason = "Final answer is missing.",
                Missing = ["final_answer"]
            };
        }

        var agent = new ChatClientAgent(chatClient, BuildSystemPrompt(), "final_answer_verifier", null, null, null, null);
        var answerJson = JsonSerializer.Serialize(answer.Value, new JsonSerializerOptions { WriteIndented = true });
        var response = await agent.RunAsync<ResultEnvelope<VerifierPayload>>(
            BuildUserPrompt(userQuery, answerJson),
            null,
            JsonOptions,
            null,
            cancellationToken);
        var envelope = response.Result
            ?? throw new InvalidOperationException("Final answer verifier returned an empty response envelope.");
        var payload = envelope.GetRequiredDataOrThrow("Final answer verifier");

        if (!payload.IsAnswer && LooksLikeComparisonRecommendation(userQuery, answer))
        {
            return new FinalAnswerVerificationResult
            {
                IsAnswer = true,
                Reason = "Heuristic override: the answer contains an explicit recommendation for a comparison-style question.",
                Missing = []
            };
        }

        return new FinalAnswerVerificationResult
        {
            IsAnswer = payload.IsAnswer,
            Reason = payload.Reason,
            Missing = payload.Missing
        };
    }

    private static string BuildSystemPrompt() =>
        """
        You are a strict final-answer verifier.
        Determine whether the candidate final answer genuinely answers the original user question.
        Return ONLY valid JSON with this exact shape:
        {"ok":true|false,"data":{"isAnswer":true|false,"reason":"short explanation","missing":["optional missing item"]}|null,"error":null|{"code":"string","message":"string","details":null}}
        Mark isAnswer=true when the answer directly addresses the core request, even if wording differs.
        If the question asks to compare or choose and the answer gives a clear recommendation with relevant justification, that usually counts as an answer.
        Mark isAnswer=false only when the answer is off-topic, materially incomplete, or avoids the actual question.
        When evaluation succeeds, set ok=true, error=null, and put the verdict into data.
        """;

    private static string BuildUserPrompt(string userQuery, string answerJson) =>
        $"Original question:\n{userQuery}\n\nCandidate final answer:\n{answerJson}";

    private static bool LooksLikeComparisonRecommendation(string question, JsonElement? answer)
    {
        if (!IsComparisonQuestion(question) || answer is not JsonElement answerElement)
            return false;

        if (answerElement.ValueKind == JsonValueKind.Object)
        {
            return answerElement.EnumerateObject().Any(property => property.Name is "betterModel" or "better_model" or "recommendedModel" or "recommended_model" or "bestModel" or "preferredModel");
        }

        return answerElement.ValueKind == JsonValueKind.String
            && LooksLikeComparisonRecommendationText(answerElement.GetString() ?? string.Empty);
    }

    private static bool IsComparisonQuestion(string question)
    {
        var normalized = question.ToLowerInvariant();
        return normalized.Contains("compare", StringComparison.Ordinal)
            || normalized.Contains("which one", StringComparison.Ordinal)
            || normalized.Contains("better", StringComparison.Ordinal)
            || normalized.Contains("best", StringComparison.Ordinal)
            || normalized.Contains("recommend", StringComparison.Ordinal);
    }

    private static bool LooksLikeComparisonRecommendationText(string answerText)
    {
        var normalized = answerText.ToLowerInvariant();
        var hasRecommendation = normalized.Contains("recommended", StringComparison.Ordinal)
            || normalized.Contains("recommend", StringComparison.Ordinal)
            || normalized.Contains("better", StringComparison.Ordinal)
            || normalized.Contains("best", StringComparison.Ordinal)
            || normalized.Contains("winner", StringComparison.Ordinal);
        if (!hasRecommendation)
            return false;

        return normalized.Contains(" vs ", StringComparison.Ordinal)
            || normalized.Contains("versus", StringComparison.Ordinal)
            || normalized.Contains("higher", StringComparison.Ordinal)
            || normalized.Contains("lower", StringComparison.Ordinal)
            || normalized.Contains("longer", StringComparison.Ordinal)
            || normalized.Contains("shorter", StringComparison.Ordinal)
            || normalized.Contains("stronger", StringComparison.Ordinal);
    }

    private sealed record VerifierPayload
    {
        public bool IsAnswer { get; init; }

        public string Reason { get; init; } = string.Empty;

        public List<string> Missing { get; init; } = [];
    }
}

