using System.Collections.Generic;
using ChatClient.Api.Client.ViewModels;

namespace ChatClient.Api.Client.Services;

public interface IChatFormatter
{
    ChatFormat FormatType { get; }
    string Format(IEnumerable<AppChatMessageViewModel> messages);
}
