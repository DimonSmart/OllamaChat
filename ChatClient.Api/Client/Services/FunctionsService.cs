using ChatClient.Shared.Models;
using ChatClient.Api.Services;

namespace ChatClient.Api.Client.Services;

public class FunctionsService(KernelService kernelService) : IFunctionsService
{
    public async Task<IReadOnlyCollection<FunctionInfo>> GetAvailableFunctionsAsync()
    {
        return await kernelService.GetAvailableFunctionsAsync();
    }
}
