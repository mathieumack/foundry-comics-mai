using MaiImageGenerator.Api.Contracts;
using MaiImageGenerator.Api.Services;
using MDev.Dotnet.Azure.ContainerApps.Authentication.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace MaiImageGenerator.Api.Controllers;

[ApiController]
[Route("api/references")]
public sealed class ReferencesController(IHttpContextAccessor httpContextAccessor, ReferenceService referenceService) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(ReferenceService.MaximumFileLength)]
    public async Task<ActionResult<ReferenceUploadResponse>> Upload(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var ownerId = httpContextAccessor.GetUserId();
        if (ownerId is null)
        {
            return Unauthorized();
        }

        try
        {
            var reference = await referenceService.UploadAsync(ownerId, file, cancellationToken);
            return Ok(new ReferenceUploadResponse(reference.Id, reference.FileName));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid reference image", Detail = exception.Message });
        }
    }

    [HttpGet("{referenceId}/content")]
    public async Task<IActionResult> Download(string referenceId, CancellationToken cancellationToken)
    {
        var ownerId = httpContextAccessor.GetUserId();
        if (ownerId is null)
        {
            return Unauthorized();
        }

        var reference = await referenceService.GetAsync(ownerId, referenceId, cancellationToken);
        if (reference is null)
        {
            return NotFound();
        }

        var content = await referenceService.OpenReadAsync(ownerId, referenceId, cancellationToken);
        return content is null ? NotFound() : File(content, reference.ContentType, reference.FileName);
    }
}