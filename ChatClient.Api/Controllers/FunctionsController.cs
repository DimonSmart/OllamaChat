using Microsoft.AspNetCore.Mvc;
using ChatClient.Shared.Models;
using ChatClient.Api.Services;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FunctionsController : ControllerBase
{
    private readonly KernelService _kernelService;

    public FunctionsController(KernelService kernelService)
    {
        _kernelService = kernelService;
    }

    [HttpGet]
    public ActionResult<IEnumerable<FunctionInfo>> GetAvailableFunctions()
    {
        var functions = _kernelService.GetAvailableFunctions();
        return Ok(functions);
    }
}
