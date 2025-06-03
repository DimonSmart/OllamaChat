using ChatClient.Shared.Models;

namespace ChatClient.Api.Client.Services;

public interface IFunctionsService
{
    Task<IReadOnlyCollection<FunctionInfo>> GetAvailableFunctionsAsync();
}
