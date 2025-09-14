using ChatClient.Api.Services;
using ChatClient.Application.Services;
using ChatClient.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using Microsoft.SemanticKernel.Plugins.Web;
using HtmlAgilityPack;

namespace ChatClient.Api.Controllers;

[ApiController]
[Route("api/agents/{agentId:guid}/files")]
public class RagFilesController : ControllerBase
{
    private readonly IRagFileService _fileService;
    private readonly IRagContentImportService _importService;
    private readonly IEnumerable<IFileConverter> _converters;

    public RagFilesController(IRagFileService fileService, IRagContentImportService importService, IEnumerable<IFileConverter> converters)
    {
        _fileService = fileService;
        _importService = importService;
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
        await _importService.AddContentAsync(agentId, content, file.FileName);
        return Ok();
    }

    [HttpPost("web")]
    public async Task<ActionResult> ImportWeb(Guid agentId, [FromBody] WebPageImportRequest request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var pageUri))
            return BadRequest("Invalid URL.");

        var tempPath = Path.GetTempFileName();
        var downloader = new WebFileDownloadPlugin();
        await downloader.DownloadToFileAsync(pageUri, tempPath, HttpContext.RequestAborted);
        var html = await System.IO.File.ReadAllTextAsync(tempPath, HttpContext.RequestAborted);
        System.IO.File.Delete(tempPath);

        var text = HtmlToText(html);
        var fileName = CreateFileName(pageUri);
        await _importService.AddContentAsync(agentId, text, fileName);
        return Ok();
    }

    private static string HtmlToText(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc.DocumentNode.InnerText;
    }

    private static string CreateFileName(Uri sourceUrl)
    {
        var hostPart = sourceUrl.Host.Replace('.', '_');
        return $"{hostPart}_{Guid.NewGuid():N}.txt";
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
        await _importService.AddContentAsync(agentId, content, fileName);
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

