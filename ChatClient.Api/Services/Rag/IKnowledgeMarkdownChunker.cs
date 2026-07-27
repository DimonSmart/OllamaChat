using ChatClient.Domain.Models;

namespace ChatClient.Api.Services.Rag;

public interface IKnowledgeMarkdownChunker
{
    IReadOnlyList<KnowledgeChunkRecord> Chunk(string fileName, string markdown, int maxTokens, int overlapTokens);
}
