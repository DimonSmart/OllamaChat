using ChatClient.Api.Services;
using ChatClient.Shared.Models;

using Microsoft.AspNetCore.Mvc;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FunctionsController(KernelService kernelService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<FunctionInfo>>> GetAvailableFunctions()
    {
        var functions = await kernelService.GetAvailableFunctionsAsync();
        return Ok(functions);
    }
}
