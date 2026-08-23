namespace ChatClient.Application.Services;

public interface IChatTitleGenerator
{
    string Generate(string firstUserMessage);
}
