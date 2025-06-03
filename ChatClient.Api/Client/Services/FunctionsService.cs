using ChatClient.Api.Services;
using ChatClient.Shared.Models;

namespace ChatClient.Api.Client.Services;

public class FunctionsService(KernelService kernelService) : IFunctionsService
{
    public async Task<IReadOnlyCollection<FunctionInfo>> GetAvailableFunctionsAsync()
    {
        return await kernelService.GetAvailableFunctionsAsync();
    }
}