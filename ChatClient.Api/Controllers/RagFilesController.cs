using ChatClient.Api.Services;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using System.Linq;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/agents/{agentId:guid}/files")]
public class RagFilesController : ControllerBase
{
    private readonly IRagFileService _fileService;
    private readonly IEnumerable<IFileConverter> _converters;

    public RagFilesController(IRagFileService fileService, IEnumerable<IFileConverter> converters)
    {
        _fileService = fileService;
        _converters = converters;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<RagFile>>> GetFiles(Guid agentId)
        => Ok(await _fileService.GetFilesAsync(agentId));

    [HttpGet("{fileName}")]
    public async Task<ActionResult<RagFile>> GetFile(Guid agentId, string fileName)
    {
        try
        {
            FileNameValidator.Validate(fileName);
        }
        catch (ArgumentException)
        {
            return BadRequest("Invalid file name.");
        }
        var file = await _fileService.GetFileAsync(agentId, fileName);
        if (file is null)
            return NotFound();
        return Ok(file);
    }

    [HttpPost]
    public async Task<ActionResult> Upload(Guid agentId, [FromForm] IFormFile file)
    {
        try
        {
            FileNameValidator.Validate(file.FileName);
        }
        catch (ArgumentException)
        {
            return BadRequest("Invalid file name.");
        }
        var ext = Path.GetExtension(file.FileName);
        var converter = _converters.FirstOrDefault(c => c.GetSupportedExtensions().Contains(ext, StringComparer.OrdinalIgnoreCase));
        if (converter is null)
            return BadRequest($"Unsupported file type {ext}");

        var content = await converter.ConvertToTextAsync(file);
        await _fileService.AddOrUpdateFileAsync(agentId, new RagFile { FileName = file.FileName, Content = content });
        return Ok();
    }

    [HttpPut("{fileName}")]
    public async Task<ActionResult> Update(Guid agentId, string fileName, [FromForm] IFormFile file)
    {
        try
        {
            FileNameValidator.Validate(fileName);
            FileNameValidator.Validate(file.FileName);
        }
        catch (ArgumentException)
        {
            return BadRequest("Invalid file name.");
        }
        var ext = Path.GetExtension(file.FileName);
        var converter = _converters.FirstOrDefault(c => c.GetSupportedExtensions().Contains(ext, StringComparer.OrdinalIgnoreCase));
        if (converter is null)
            return BadRequest($"Unsupported file type {ext}");

        var content = await converter.ConvertToTextAsync(file);
        await _fileService.AddOrUpdateFileAsync(agentId, new RagFile { FileName = fileName, Content = content });
        return Ok();
    }

    [HttpDelete("{fileName}")]
    public async Task<ActionResult> Delete(Guid agentId, string fileName)
    {
        try
        {
            FileNameValidator.Validate(fileName);
        }
        catch (ArgumentException)
        {
            return BadRequest("Invalid file name.");
        }
        await _fileService.DeleteFileAsync(agentId, fileName);
        return NoContent();
    }
}

