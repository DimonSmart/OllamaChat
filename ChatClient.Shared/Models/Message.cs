using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ChatClient.Shared.Models;

public class Message : INotifyPropertyChanged
{
    private string _content;

    public string Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                _content = value;
                OnPropertyChanged();
            }
        }
    }

    public DateTime MsgDateTime { get; private set; }
    public ChatRole Role { get; set; }
    public Message(string content, DateTime msgDateTime, ChatRole role)
    {
        _content = content;
        MsgDateTime = msgDateTime;
        Role = role;
    }

    public override string ToString()
    {
        return $"{MsgDateTime} {Content}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
