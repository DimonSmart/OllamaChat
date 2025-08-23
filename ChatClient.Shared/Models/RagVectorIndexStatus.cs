namespace ChatClient.Shared.Models;

public record RagVectorIndexStatus(Guid AgentId, string FileName, int Processed, int Total);

