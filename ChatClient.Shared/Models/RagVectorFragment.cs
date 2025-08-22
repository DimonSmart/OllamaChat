namespace ChatClient.Shared.Models;

public class RagVectorFragment
{
    public string Id { get; set; } = string.Empty;
    public long Offset { get; set; }
    public int Length { get; set; }
    public float[] Vector { get; set; } = Array.Empty<float>();
}
