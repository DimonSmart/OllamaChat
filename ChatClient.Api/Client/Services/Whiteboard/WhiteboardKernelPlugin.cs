using System;
using System.ComponentModel;
using System.Text;
using System.Threading.Tasks;
using ChatClient.Domain.Models;
using Microsoft.SemanticKernel;

namespace ChatClient.Api.Client.Services.Whiteboard;

internal sealed class WhiteboardKernelPlugin
{
    private readonly WhiteboardState _state;
    private readonly Func<string, Task>? _onUpdate;

    public WhiteboardKernelPlugin(WhiteboardState state, Func<string, Task>? onUpdate)
    {
        _state = state;
        _onUpdate = onUpdate;
    }

    [KernelFunction, Description("Add or update a note on the shared whiteboard for this chat session.")]
    public async Task<string> AddNoteAsync(
        [Description("Text to record on the whiteboard.")] string note,
        [Description("Optional author for the note.")] string? author = null)
    {
        var entry = _state.Add(note, author);
        await NotifyAsync($"Whiteboard updated with entry {entry.Id}.");
        return BuildSnapshot();
    }

    [KernelFunction, Description("Return all whiteboard notes as a markdown list for context sharing.")]
    public string GetNotes() => BuildSnapshot();

    [KernelFunction, Description("Clear every note from the shared whiteboard.")]
    public async Task<string> ClearAsync()
    {
        _state.Clear();
        await NotifyAsync("Whiteboard cleared.");
        return "Whiteboard cleared.";
    }

    private async Task NotifyAsync(string message)
    {
        if (_onUpdate != null)
        {
            await _onUpdate(message);
        }
    }

    private string BuildSnapshot()
    {
        if (_state.Notes.Count == 0)
        {
            return "Whiteboard is empty.";
        }

        var builder = new StringBuilder();
        builder.AppendLine("Current whiteboard notes:");

        var index = 1;
        foreach (var note in _state.Notes)
        {
            builder.Append("- ");
            builder.Append(index++);
            builder.Append(". ");

            if (!string.IsNullOrWhiteSpace(note.Author))
            {
                builder.Append('[');
                builder.Append(note.Author);
                builder.Append("] ");
            }

            builder.Append(note.Content);
            builder.Append(" (created at ");
            builder.Append(note.CreatedAt.ToLocalTime().ToString("u"));
            builder.AppendLine(")");
        }

        return builder.ToString();
    }
}
