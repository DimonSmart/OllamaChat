namespace ChatClient.Domain.Models;

public record WhiteboardNote(Guid Id, string Content, DateTimeOffset CreatedAt, string? Author);
