using ChatClient.Api.PlanningRuntime.Common;
using ChatClient.Api.PlanningRuntime.Planning;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;

namespace ChatClient.Api.PlanningRuntime.Verification;

public interface IFinalAnswerVerifier
{
    Task<FinalAnswerVerificationResult> VerifyAsync(
        string userQuery,
        JsonElement? answer,
        ResultContract? resultContract = null,
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
        ResultContract? resultContract = null,
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
        var answerJson = PlanningJson.SerializeIndented(answer.Value);
        var response = await agent.RunAsync<ResultEnvelope<VerifierPayload>>(
            BuildUserPrompt(userQuery, answerJson, resultContract),
            null,
            JsonOptions,
            null,
            cancellationToken);
        var envelope = response.Result
            ?? throw new InvalidOperationException("Final answer verifier returned an empty response envelope.");
        var payload = envelope.GetRequiredDataOrThrow("Final answer verifier");

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
        When a result contract is provided, also verify that the answer satisfies its completeness, evidence, and formatting requirements.
        Return ONLY valid JSON with this exact shape:
        {"ok":true|false,"data":{"isAnswer":true|false,"reason":"short explanation","missing":["optional missing item"]}|null,"error":null|{"code":"string","message":"string","details":null}}
        Mark isAnswer=true when the answer directly addresses the core request, even if wording differs.
        If the question asks to compare or choose and the answer gives a clear recommendation with relevant justification, that usually counts as an answer.
        Mark isAnswer=false only when the answer is off-topic, materially incomplete, or avoids the actual question.
        When a result contract is present, mark isAnswer=false and populate missing[] if any completeness requirement is clearly unmet.
        When evaluation succeeds, set ok=true, error=null, and put the verdict into data.
        """;

    private static string BuildUserPrompt(string userQuery, string answerJson, ResultContract? contract)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Original question:");
        sb.AppendLine(userQuery);

        if (contract is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Result contract:");
            sb.AppendLine($"  Expected artifact type: {contract.ExpectedArtifactType}");

            if (contract.CompletenessRequirements.Count > 0)
            {
                sb.AppendLine("  Completeness requirements:");
                foreach (var req in contract.CompletenessRequirements)
                    sb.AppendLine($"    - {req}");
            }

            if (!string.IsNullOrWhiteSpace(contract.EvidenceRequirement))
                sb.AppendLine($"  Evidence requirement: {contract.EvidenceRequirement}");

            if (!string.IsNullOrWhiteSpace(contract.FormattingRequirements))
                sb.AppendLine($"  Formatting requirements: {contract.FormattingRequirements}");

            if (!string.IsNullOrWhiteSpace(contract.LanguagePolicy))
                sb.AppendLine($"  Language policy: {contract.LanguagePolicy}");
        }

        sb.AppendLine();
        sb.AppendLine("Candidate final answer:");
        sb.Append(answerJson);
        return sb.ToString();
    }

    private sealed record VerifierPayload
    {
        public bool IsAnswer { get; init; }

        public string Reason { get; init; } = string.Empty;

        public List<string> Missing { get; init; } = [];
    }
}

