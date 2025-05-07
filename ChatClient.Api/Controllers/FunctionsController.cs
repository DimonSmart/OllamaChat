using Microsoft.AspNetCore.Mvc;
using ChatClient.Shared.Models;
using ChatClient.Api.Services;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FunctionsController(KernelService kernelService) : ControllerBase
{
    [HttpGet]
    public ActionResult<IEnumerable<FunctionInfo>> GetAvailableFunctions()
    {
        var functions = kernelService.GetAvailableFunctions();
        return Ok(functions);
    }
}
