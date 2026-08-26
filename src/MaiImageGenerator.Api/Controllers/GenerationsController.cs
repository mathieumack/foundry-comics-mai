using MaiImageGenerator.Api.Contracts;
using MaiImageGenerator.Api.Services;
using MDev.Dotnet.Azure.ContainerApps.Authentication.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace MaiImageGenerator.Api.Controllers;

[ApiController]
[Route("api/generations")]
public sealed class GenerationsController(IHttpContextAccessor httpContextAccessor, GenerationService generationService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<GenerationResponse>> Create(
        CreateGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var ownerId = httpContextAccessor.GetUserId();
        if (ownerId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await generationService.GenerateAsync(ownerId, request, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid generation request", Detail = exception.Message });
        }
    }

    [HttpGet("{generationId:guid}")]
    public async Task<ActionResult<GenerationResponse>> Get(Guid generationId, CancellationToken cancellationToken)
    {
        var ownerId = httpContextAccessor.GetUserId();
        if (ownerId is null)
        {
            return Unauthorized();
        }

        var result = await generationService.GetAsync(ownerId, generationId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("{generationId:guid}/board")]
    public Task<IActionResult> DownloadBoard(Guid generationId, CancellationToken cancellationToken) =>
        DownloadArtifact(generationId, null, cancellationToken);

    [HttpGet("{generationId:guid}/panels/{panelNumber:int}")]
    public Task<IActionResult> DownloadPanel(
        Guid generationId,
        int panelNumber,
        CancellationToken cancellationToken) =>
        DownloadArtifact(generationId, panelNumber, cancellationToken);

    private async Task<IActionResult> DownloadArtifact(
        Guid generationId,
        int? panelNumber,
        CancellationToken cancellationToken)
    {
        var ownerId = httpContextAccessor.GetUserId();
        if (ownerId is null)
        {
            return Unauthorized();
        }

        var artifact = await generationService.OpenArtifactAsync(ownerId, generationId, panelNumber, cancellationToken);
        return artifact is null ? NotFound() : File(artifact.Value.Content, artifact.Value.ContentType);
    }
}