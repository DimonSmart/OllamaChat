using ChatClient.Shared.Models;

namespace ChatClient.Api.Services;

public interface IServerConnectionTestService
{
    Task<ServerConnectionTestResult> TestConnectionAsync(LlmServerConfig server, CancellationToken cancellationToken = default);
}

public class ServerConnectionTestResult
{
    public bool IsSuccessful { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Details { get; init; }
    
    public static ServerConnectionTestResult Success(string? details = null) => new()
    {
        IsSuccessful = true,
        Details = details
    };
    
    public static ServerConnectionTestResult Failure(string errorMessage, string? details = null) => new()
    {
        IsSuccessful = false,
        ErrorMessage = errorMessage,
        Details = details
    };
}