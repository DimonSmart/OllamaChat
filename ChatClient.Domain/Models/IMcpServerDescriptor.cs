namespace ChatClient.Domain.Models;

public interface IMcpServerDescriptor
{
    Guid? Id { get; }
    string Name { get; }
    string Description { get; }
}
