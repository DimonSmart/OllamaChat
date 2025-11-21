using System;
using System.Collections.ObjectModel;

namespace ChatClient.Domain.Models;

public class WhiteboardState
{
    private readonly List<WhiteboardNote> _notes = [];
    public ReadOnlyCollection<WhiteboardNote> Notes => _notes.AsReadOnly();

    public WhiteboardNote Add(string content, string? author)
    {
        var trimmed = content?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ArgumentException("Note content cannot be empty.", nameof(content));

        var note = new WhiteboardNote(Guid.NewGuid(), trimmed, DateTimeOffset.UtcNow, author);
        _notes.Add(note);
        return note;
    }

    public void Clear() => _notes.Clear();
}
