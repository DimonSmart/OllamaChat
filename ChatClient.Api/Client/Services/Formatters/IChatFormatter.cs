using ChatClient.Api.Client.ViewModels;

namespace ChatClient.Api.Client.Services.Formatters;

public interface IChatFormatter
{
    ChatFormat FormatType { get; }
    string Format(IEnumerable<AppChatMessageViewModel> messages);
}
