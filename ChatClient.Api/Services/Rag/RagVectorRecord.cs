using Microsoft.Extensions.VectorData;


namespace ChatClient.Api.Services.Rag;

public sealed class RagVectorRecord
{
    [VectorStoreKey]
    public string Id { get; set; } = default!;

    [VectorStoreData]
    public string File { get; set; } = default!;

    [VectorStoreData]
    public int Index { get; set; }

    [VectorStoreData]
    public string Text { get; set; } = default!;

    [VectorStoreVector(1536)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
