using Azure;
using MaiImageGenerator.Api.Services;
using MDev.Dotnet.Azure.ContainerApps.Authentication.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace MaiImageGenerator.Api.Controllers;

[ApiController]
[Route("api/projects/{projectId:guid}/export/epub")]
public sealed class EpubExportsController(
    IHttpContextAccessor httpContextAccessor,
    EpubExportService exportService,
    ILogger<EpubExportsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Download(Guid projectId, CancellationToken cancellationToken)
    {
        var ownerId = httpContextAccessor.GetUserId();
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return Unauthorized();
        }

        Response.Headers.CacheControl = "no-store";
        try
        {
            var export = await exportService.ExportAsync(ownerId, projectId, cancellationToken);
            return export is null ? NotFound() : File(export.Content, EpubExportService.ContentType, export.FileName);
        }
        catch (EpubExportException exception)
        {
            return Conflict(new ProblemDetails { Title = "EPUB export unavailable", Detail = exception.Message });
        }
        catch (Exception exception) when (exception is IOException or RequestFailedException)
        {
            logger.LogError(exception, "EPUB export failed for project {ProjectId}.", projectId);
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "EPUB export failed",
                detail: "The project or its images could not be read. Retry export; if it still fails, regenerate missing images.");
        }
    }
}
