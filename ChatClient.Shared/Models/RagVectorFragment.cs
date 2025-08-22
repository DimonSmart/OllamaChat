namespace ChatClient.Shared.Models;

public class RagVectorFragment
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public float[] Vector { get; set; } = Array.Empty<float>();
}
