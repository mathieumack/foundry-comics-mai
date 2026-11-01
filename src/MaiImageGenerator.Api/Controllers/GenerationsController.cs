using MaiImageGenerator.Api.Contracts;
using MaiImageGenerator.Api.Services;
using MaiImageGenerator.Shared.Services;
using MDev.Dotnet.Azure.ContainerApps.Authentication.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MaiImageGenerator.Api.Controllers;

[ApiController]
[Route("api/generations")]
public sealed class GenerationsController(
    IHttpContextAccessor httpContextAccessor,
    GenerationService generationService,
    ProjectScaffolder projectScaffolder,
    ScaffoldTraceStore scaffoldTraceStore) : ControllerBase
{
    [HttpPost("scaffold")]
    public async Task<ActionResult<ProjectScaffoldResponse>> CreateScaffold(
        CreateProjectScaffoldRequest request,
        CancellationToken cancellationToken)
    {
        var ownerId = httpContextAccessor.GetUserId();
        if (ownerId is null)
        {
            return Unauthorized();
        }

        try
        {
            var scaffold = await projectScaffolder.CreateAsync(request, cancellationToken);
            return Ok(await scaffoldTraceStore.SaveAsync(ownerId, scaffold, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid scaffold request", Detail = exception.Message });
        }
        catch (FoundryGenerationException exception)
        {
            return Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Project scaffolding failed",
                detail: exception.Message);
        }
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GenerationSummaryResponse>>> List(CancellationToken cancellationToken)
    {
        var ownerId = httpContextAccessor.GetUserId();
        if (ownerId is null)
        {
            return Unauthorized();
        }

        return Ok(await generationService.ListAsync(ownerId, cancellationToken));
    }

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

    [HttpPost("{generationId:guid}/panels/{panelNumber:int}/attempts")]
    public Task<ActionResult<GenerationResponse>> CreatePanelAttempt(Guid generationId, int panelNumber,
        CreatePanelAttemptRequest request, CancellationToken cancellationToken) =>
        ExecutePanelCommand(ownerId => generationService.CreatePanelAttemptAsync(
            ownerId, generationId, panelNumber, request, cancellationToken));

    [HttpPut("{generationId:guid}/panels/{panelNumber:int}/selection")]
    public Task<ActionResult<GenerationResponse>> SelectPanelVersion(Guid generationId, int panelNumber,
        SelectPanelVersionRequest request, CancellationToken cancellationToken) =>
        ExecutePanelCommand(ownerId => generationService.SelectPanelVersionAsync(
            ownerId, generationId, panelNumber, request, cancellationToken));

    [HttpPost("{generationId:guid}/commands/{commandId:guid}/resume")]
    public Task<ActionResult<GenerationResponse>> ResumePanelCommand(Guid generationId, Guid commandId,
        CancellationToken cancellationToken) =>
        ExecutePanelCommand(ownerId => generationService.ResumePanelCommandAsync(
            ownerId, generationId, commandId, cancellationToken));

    [HttpGet("{generationId:guid}/panels/{panelNumber:int}/versions/{versionId:guid}/image")]
    public async Task<IActionResult> DownloadPanelVersion(Guid generationId, int panelNumber, Guid versionId,
        CancellationToken cancellationToken)
    {
        var ownerId = httpContextAccessor.GetUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Unauthorized();
        var artifact = await generationService.OpenPanelVersionAsync(
            ownerId, generationId, panelNumber, versionId, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return artifact is null ? NotFound() : File(artifact.Value.Content, artifact.Value.ContentType);
    }

    private async Task<ActionResult<GenerationResponse>> ExecutePanelCommand(
        Func<string, Task<GenerationResponse?>> execute)
    {
        var ownerId = httpContextAccessor.GetUserId();
        if (string.IsNullOrWhiteSpace(ownerId)) return Unauthorized();
        try
        {
            var result = await execute(ownerId);
            return result is null ? NotFound() : Ok(result);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid panel command", Detail = exception.Message });
        }
        catch (PanelCommandConflictException exception)
        {
            return Conflict(new ProblemDetails { Title = "Panel command conflict", Detail = exception.Message });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new ProblemDetails { Title = "The board changed", Detail = "Refresh before trying again." });
        }
        catch (PanelCommandDispatchException)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Panel command not queued",
                detail: "The accepted command remains pending. Retry with the same command ID.");
        }
    }

    [HttpGet("{generationId:guid}/references/{referenceId}/content")]
    public async Task<IActionResult> DownloadReference(
        Guid generationId,
        string referenceId,
        CancellationToken cancellationToken)
    {
        var ownerId = httpContextAccessor.GetUserId();
        if (ownerId is null)
        {
            return Unauthorized();
        }
        var reference = await generationService.OpenReferenceAsync(ownerId, generationId, referenceId, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return reference is null ? NotFound() : File(reference.Value.Content, reference.Value.ContentType);
    }

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
        Response.Headers.CacheControl = "no-store";
        return artifact is null ? NotFound() : File(artifact.Value.Content, artifact.Value.ContentType);
    }
}