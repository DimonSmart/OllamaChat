namespace ChatClient.Domain.Models;

public class RagVectorFragment
{
    public int Index { get; set; }
    public string Text { get; set; } = string.Empty;
    public float[] Vector { get; set; } = Array.Empty<float>();
}
