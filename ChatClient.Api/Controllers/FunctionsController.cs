using ChatClient.Api.Services;
using ChatClient.Domain.Models;

using Microsoft.AspNetCore.Mvc;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FunctionsController(KernelService kernelService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<FunctionInfo>>> GetAvailableFunctions(CancellationToken cancellationToken)
    {
        var functions = await kernelService.GetAvailableFunctionsAsync(cancellationToken);
        return Ok(functions);
    }
}
