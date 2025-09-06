using ChatClient.Api.Services;
using ChatClient.Shared.Models;
using ChatClient.Shared.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/agents/{id:guid}/files")]
public class RagFilesController : ControllerBase
{
    private readonly IRagFileService _fileService;
    private readonly IFileConverter _converter;

    public RagFilesController(IRagFileService fileService, IFileConverter converter)
    {
        _fileService = fileService;
        _converter = converter;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<RagFile>>> GetFiles(Guid id)
        => Ok(await _fileService.GetFilesAsync(id));

    [HttpGet("{fileName}")]
    public async Task<ActionResult<RagFile>> GetFile(Guid id, string fileName)
    {
        try
        {
            FileNameValidator.Validate(fileName);
        }
        catch (ArgumentException)
        {
            return BadRequest("Invalid file name.");
        }
        var file = await _fileService.GetFileAsync(id, fileName);
        if (file is null)
            return NotFound();
        return Ok(file);
    }

    [HttpPost]
    public async Task<ActionResult> Upload(Guid id, [FromForm] IFormFile file)
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
        if (!_converter.GetSupportedExtensions().Contains(ext, StringComparer.OrdinalIgnoreCase))
            return BadRequest($"Unsupported file type {ext}");

        var content = await _converter.ConvertToTextAsync(file);
        await _fileService.AddOrUpdateFileAsync(id, new RagFile { FileName = file.FileName, Content = content });
        return Ok();
    }

    [HttpPut("{fileName}")]
    public async Task<ActionResult> Update(Guid id, string fileName, [FromForm] IFormFile file)
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
        if (!_converter.GetSupportedExtensions().Contains(ext, StringComparer.OrdinalIgnoreCase))
            return BadRequest($"Unsupported file type {ext}");

        var content = await _converter.ConvertToTextAsync(file);
        await _fileService.AddOrUpdateFileAsync(id, new RagFile { FileName = fileName, Content = content });
        return Ok();
    }

    [HttpDelete("{fileName}")]
    public async Task<ActionResult> Delete(Guid id, string fileName)
    {
        try
        {
            FileNameValidator.Validate(fileName);
        }
        catch (ArgumentException)
        {
            return BadRequest("Invalid file name.");
        }
        await _fileService.DeleteFileAsync(id, fileName);
        return NoContent();
    }
}

