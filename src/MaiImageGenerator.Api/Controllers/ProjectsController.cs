using MaiImageGenerator.Api.Contracts;
using MaiImageGenerator.Api.Services;
using MDev.Dotnet.Azure.ContainerApps.Authentication.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace MaiImageGenerator.Api.Controllers;

[ApiController]
[Route("api/projects")]
public sealed class ProjectsController(
    IHttpContextAccessor httpContextAccessor,
    ProjectService projectService,
    GenerationService generationService,
    ReferenceService referenceService,
    BookService bookService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ProjectResponse>> Create(
        CreateProjectRequest request,
        CancellationToken cancellationToken)
    {
        var ownerId = GetOwnerId();
        if (ownerId is null)
        {
            return Unauthorized();
        }

        try
        {
            var project = await projectService.CreateAsync(ownerId, request, cancellationToken);
            return CreatedAtAction(nameof(Get), new { projectId = project.ProjectId }, project);
        }
        catch (ArgumentException exception)
        {
            return InvalidProject(exception);
        }
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectResponse>>> List(CancellationToken cancellationToken)
    {
        var ownerId = GetOwnerId();
        return ownerId is null
            ? Unauthorized()
            : Ok(await projectService.ListAsync(ownerId, cancellationToken));
    }

    [HttpGet("{projectId:guid}")]
    public async Task<ActionResult<ProjectResponse>> Get(Guid projectId, CancellationToken cancellationToken)
    {
        var result = await GetProjectAsync(projectId, cancellationToken);
        return result.Result ?? Ok(ProjectService.ToResponse(result.Project!));
    }

    [HttpPut("{projectId:guid}")]
    public async Task<ActionResult<ProjectResponse>> Update(
        Guid projectId,
        CreateProjectRequest request,
        CancellationToken cancellationToken)
    {
        var result = await GetProjectAsync(projectId, cancellationToken);
        if (result.Result is not null)
        {
            return result.Result;
        }

        try
        {
            return Ok(await projectService.UpdateAsync(result.Project!, request, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return InvalidProject(exception);
        }
    }

    [HttpPost("{projectId:guid}/characters")]
    public async Task<ActionResult<ProjectCharacterResponse>> AddCharacter(
        Guid projectId,
        ProjectCharacterRequest request,
        CancellationToken cancellationToken)
    {
        var result = await GetProjectAsync(projectId, cancellationToken);
        if (result.Result is not null)
        {
            return result.Result;
        }

        try
        {
            var character = await projectService.AddCharacterAsync(result.Project!, request, cancellationToken);
            return CreatedAtAction(
                nameof(GetCharacter),
                new { projectId, characterId = character.CharacterId },
                character);
        }
        catch (ArgumentException exception)
        {
            return InvalidCharacter(exception);
        }
    }

    [HttpGet("{projectId:guid}/characters")]
    public async Task<ActionResult<IReadOnlyList<ProjectCharacterResponse>>> ListCharacters(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var result = await GetProjectAsync(projectId, cancellationToken);
        return result.Result ?? Ok(result.Project!.Characters.Select(ProjectService.ToResponse).ToArray());
    }

    [HttpGet("{projectId:guid}/characters/{characterId}")]
    public async Task<ActionResult<ProjectCharacterResponse>> GetCharacter(
        Guid projectId,
        string characterId,
        CancellationToken cancellationToken)
    {
        var result = await GetProjectAsync(projectId, cancellationToken);
        if (result.Result is not null)
        {
            return result.Result;
        }

        var character = ProjectService.FindCharacter(result.Project!, characterId);
        return character is null ? NotFound() : Ok(ProjectService.ToResponse(character));
    }

    [HttpPut("{projectId:guid}/characters/{characterId}")]
    public async Task<ActionResult<ProjectCharacterResponse>> UpdateCharacter(
        Guid projectId,
        string characterId,
        ProjectCharacterRequest request,
        CancellationToken cancellationToken)
    {
        var result = await GetProjectAsync(projectId, cancellationToken);
        if (result.Result is not null)
        {
            return result.Result;
        }

        try
        {
            var character = await projectService.UpdateCharacterAsync(
                result.Project!, characterId, request, cancellationToken);
            return character is null ? NotFound() : Ok(character);
        }
        catch (ArgumentException exception)
        {
            return InvalidCharacter(exception);
        }
    }

    [HttpDelete("{projectId:guid}/characters/{characterId}")]
    public async Task<IActionResult> DeleteCharacter(
        Guid projectId,
        string characterId,
        CancellationToken cancellationToken)
    {
        var result = await GetProjectAsync(projectId, cancellationToken);
        if (result.Result is not null)
        {
            return result.Result;
        }

        return await projectService.DeleteCharacterAsync(result.Project!, characterId, cancellationToken)
            ? NoContent()
            : NotFound();
    }

    [HttpPost("{projectId:guid}/characters/{characterId}/references")]
    [RequestSizeLimit(ReferenceService.MaximumFileLength)]
    public async Task<ActionResult<ReferenceUploadResponse>> UploadReference(
        Guid projectId,
        string characterId,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var result = await GetProjectAsync(projectId, cancellationToken);
        if (result.Result is not null)
        {
            return result.Result;
        }

        var character = ProjectService.FindCharacter(result.Project!, characterId);
        if (character is null)
        {
            return NotFound();
        }

        try
        {
            var reference = await referenceService.UploadAsync(result.OwnerId!, file, cancellationToken);
            await projectService.AddReferenceAsync(result.Project!, character, reference, cancellationToken);
            return Ok(new ReferenceUploadResponse(reference.Id, reference.FileName));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid reference image", Detail = exception.Message });
        }
    }

    [HttpGet("{projectId:guid}/characters/{characterId}/references/{referenceId}/content")]
    public async Task<IActionResult> DownloadReference(
        Guid projectId,
        string characterId,
        string referenceId,
        CancellationToken cancellationToken)
    {
        var result = await GetProjectAsync(projectId, cancellationToken);
        if (result.Result is not null)
        {
            return result.Result;
        }

        if (!ProjectService.HasReference(result.Project!, characterId, referenceId))
        {
            return NotFound();
        }

        var reference = await referenceService.GetAsync(result.OwnerId!, referenceId, cancellationToken);
        if (reference is null)
        {
            return NotFound();
        }

        var content = await referenceService.OpenReadAsync(result.OwnerId!, referenceId, cancellationToken);
        return content is null ? NotFound() : File(content, reference.ContentType, reference.FileName);
    }

    [HttpGet("{projectId:guid}/boards")]
    public async Task<ActionResult<IReadOnlyList<GenerationSummaryResponse>>> ListBoards(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var result = await GetProjectAsync(projectId, cancellationToken);
        return result.Result ?? Ok(await generationService.ListAsync(
            result.OwnerId!, result.Project!.Id, cancellationToken));
    }

    [HttpPost("{projectId:guid}/boards")]
    public async Task<ActionResult<GenerationResponse>> CreateBoard(
        Guid projectId,
        CreateBoardRequest request,
        CancellationToken cancellationToken)
    {
        var result = await GetProjectAsync(projectId, cancellationToken);
        if (result.Result is not null)
        {
            return result.Result;
        }

        if (result.Project!.Type != MaiImageGenerator.Shared.Data.Entities.ProjectType.Comic)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid board request", Detail = "Books do not contain comic boards." });
        }

        try
        {
            var board = await generationService.CreateBoardAsync(
                result.OwnerId!, result.Project!, request, cancellationToken);
            return CreatedAtAction(
                nameof(GenerationsController.Get),
                "Generations",
                new { generationId = board.GenerationId },
                board);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid board request", Detail = exception.Message });
        }
    }

    [HttpGet("{projectId:guid}/book/plan")]
        public async Task<ActionResult<BookPlanResponse>> GetBookPlan(
            Guid projectId,
            CancellationToken cancellationToken)
        {
            var result = await GetProjectAsync(projectId, cancellationToken);
            if (result.Result is not null)
            {
                return result.Result;
            }

            try
            {
                return Ok(bookService.GetPlan(result.Project!));
            }
            catch (ArgumentException exception)
            {
                return InvalidBook(exception);
            }
        }

        [HttpPost("{projectId:guid}/book/plan/generate")]
        public async Task<ActionResult<BookPlanResponse>> GenerateBookPlan(
            Guid projectId,
            GenerateBookPlanRequest request,
            CancellationToken cancellationToken)
        {
            var result = await GetProjectAsync(projectId, cancellationToken);
            if (result.Result is not null)
            {
                return result.Result;
            }

            try
            {
                return Accepted(await bookService.GeneratePlanAsync(
                    result.Project!,
                    request.ChapterCount,
                    cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return InvalidBook(exception);
            }
        }

        [HttpPut("{projectId:guid}/book/plan")]
        public async Task<ActionResult<BookPlanResponse>> UpdateBookPlan(
            Guid projectId,
            UpdateBookPlanRequest request,
            CancellationToken cancellationToken)
        {
            var result = await GetProjectAsync(projectId, cancellationToken);
            if (result.Result is not null)
            {
                return result.Result;
            }

            try
            {
                return Ok(await bookService.UpdatePlanAsync(result.Project!, request, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return InvalidBook(exception);
            }
        }

        [HttpPost("{projectId:guid}/book/chapters/{chapterId}/prose/generate")]
        public async Task<ActionResult<BookPlanResponse>> GenerateChapterProse(
            Guid projectId,
            string chapterId,
            CancellationToken cancellationToken)
        {
            var result = await GetProjectAsync(projectId, cancellationToken);
            if (result.Result is not null)
            {
                return result.Result;
            }

            try
            {
                return Ok(await bookService.GenerateProseAsync(result.Project!, chapterId, cancellationToken));
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (ArgumentException exception)
            {
                return InvalidBook(exception);
            }
        }

        [HttpPost("{projectId:guid}/book/chapters/{chapterId}/image/generate")]
        public async Task<ActionResult<BookPlanResponse>> GenerateChapterImage(
            Guid projectId,
            string chapterId,
            CancellationToken cancellationToken)
        {
            var result = await GetProjectAsync(projectId, cancellationToken);
            if (result.Result is not null)
            {
                return result.Result;
            }

            try
            {
                return Ok(await bookService.GenerateImageAsync(result.Project!, chapterId, cancellationToken));
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (ArgumentException exception)
            {
                return InvalidBook(exception);
            }
        }

        [HttpPost("{projectId:guid}/book/images/generate")]
        public async Task<ActionResult<BookPlanResponse>> GenerateRemainingChapterImages(
            Guid projectId,
            CancellationToken cancellationToken)
        {
            var result = await GetProjectAsync(projectId, cancellationToken);
            if (result.Result is not null)
            {
                return result.Result;
            }

            try
            {
                return Ok(await bookService.GenerateRemainingImagesAsync(result.Project!, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return InvalidBook(exception);
            }
        }

        [HttpGet("{projectId:guid}/book/chapters/{chapterId}/image")]
        public async Task<IActionResult> GetChapterImage(
            Guid projectId,
            string chapterId,
            CancellationToken cancellationToken)
        {
            var result = await GetProjectAsync(projectId, cancellationToken);
            if (result.Result is not null)
            {
                return result.Result;
            }

            try
            {
                var content = await bookService.OpenImageAsync(result.Project!, chapterId, cancellationToken);
                return content is null ? NotFound() : File(content, "image/png");
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (ArgumentException exception)
            {
                return InvalidBook(exception);
            }
        }

    private string? GetOwnerId() => httpContextAccessor.GetUserId();

    private async Task<ProjectLookup> GetProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var ownerId = GetOwnerId();
        if (ownerId is null)
        {
            return new(null, null, Unauthorized());
        }

        var project = await projectService.FindAsync(ownerId, projectId, cancellationToken);
        return project is null
            ? new(ownerId, null, NotFound())
            : new(ownerId, project, null);
    }

    private BadRequestObjectResult InvalidProject(ArgumentException exception) =>
        BadRequest(new ProblemDetails { Title = "Invalid project request", Detail = exception.Message });

    private BadRequestObjectResult InvalidCharacter(ArgumentException exception) =>
        BadRequest(new ProblemDetails { Title = "Invalid character request", Detail = exception.Message });

    private BadRequestObjectResult InvalidBook(ArgumentException exception) =>
        BadRequest(new ProblemDetails { Title = "Invalid book request", Detail = exception.Message });

    private sealed record ProjectLookup(
        string? OwnerId,
        MaiImageGenerator.Shared.Data.Entities.ProjectDocument? Project,
        ActionResult? Result);
}
