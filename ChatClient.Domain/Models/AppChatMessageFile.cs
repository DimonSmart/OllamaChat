using System.Text.Json.Serialization;

namespace ChatClient.Domain.Models;

public class AppChatMessageFile
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public byte[] Data { get; set; } = [];

    [JsonConstructor]
    public AppChatMessageFile()
    {
        Id = Guid.NewGuid();
    }

    public AppChatMessageFile(string name, long size, string contentType, byte[] data)
    {
        Id = Guid.NewGuid();
        Name = name;
        Size = size;
        ContentType = contentType;
        Data = data;
    }
}
