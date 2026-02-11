using ChatClient.Domain.Models;

namespace ChatClient.Application.Services.Agentic;

public sealed class AgenticRagContextResult
{
    public string ContextText { get; init; } = string.Empty;

    public IReadOnlyList<RagSearchResult> Sources { get; init; } = [];

    public bool HasContext => !string.IsNullOrWhiteSpace(ContextText);
}
