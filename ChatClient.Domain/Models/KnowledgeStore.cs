namespace ChatClient.Domain.Models;

public sealed class KnowledgeStore
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public KnowledgeStoreIndexConfiguration Configuration { get; set; } = new();
    public KnowledgeStoreIndexMetadata Index { get; set; } = new();
    public List<KnowledgeDocument> Documents { get; set; } = [];
}

public sealed class KnowledgeStoreIndexConfiguration : IEquatable<KnowledgeStoreIndexConfiguration>
{
    public Guid ServerId { get; set; }
    public string Model { get; set; } = string.Empty;
    public int Dimensions { get; set; }
    public int MaxTokensPerChunk { get; set; } = 512;
    public int OverlapTokens { get; set; } = 64;
    public string IngestionVersion { get; set; } = "data-ingestion-token-v1";

    public bool Equals(KnowledgeStoreIndexConfiguration? other) => other is not null &&
        ServerId == other.ServerId && Model == other.Model && Dimensions == other.Dimensions &&
        MaxTokensPerChunk == other.MaxTokensPerChunk && OverlapTokens == other.OverlapTokens &&
        IngestionVersion == other.IngestionVersion;
    public override bool Equals(object? obj) => Equals(obj as KnowledgeStoreIndexConfiguration);
    public override int GetHashCode() => HashCode.Combine(ServerId, Model, Dimensions, MaxTokensPerChunk, OverlapTokens, IngestionVersion);
    public KnowledgeStoreIndexConfiguration Clone() => (KnowledgeStoreIndexConfiguration)MemberwiseClone();
}

public enum KnowledgeStoreIndexState { NotIndexed, Indexing, Ready, Outdated, Failed }

public sealed class KnowledgeStoreIndexMetadata
{
    public KnowledgeStoreIndexState State { get; set; } = KnowledgeStoreIndexState.NotIndexed;
    public KnowledgeStoreIndexConfiguration? IndexedConfiguration { get; set; }
    public string? LastError { get; set; }
    public DateTime? CompletedUtc { get; set; }
}

public sealed class KnowledgeDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Content { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
