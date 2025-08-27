using Microsoft.Extensions.VectorData;

namespace ChatClient.Api.Services;

[VectorStoreRecord]
public sealed class RagVectorRecord
{
    [VectorStoreRecordKey]
    public string Id { get; set; } = default!;

    [VectorStoreRecordData]
    public string File { get; set; } = default!;

    [VectorStoreRecordData]
    public int Index { get; set; }

    [VectorStoreRecordData]
    public string Text { get; set; } = default!;

    [VectorStoreRecordVector(1536)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
