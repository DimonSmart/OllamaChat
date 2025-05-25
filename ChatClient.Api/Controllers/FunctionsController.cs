using Microsoft.AspNetCore.Mvc;
using ChatClient.Shared.Models;
using ChatClient.Api.Services;

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
