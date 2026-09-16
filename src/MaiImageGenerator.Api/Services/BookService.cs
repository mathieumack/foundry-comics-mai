using System.Text.Json;
using Azure.Storage.Blobs;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Api.Contracts;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace MaiImageGenerator.Api.Services;

public sealed class BookService
{
    private const int MaximumChapterCount = 30;
    private readonly AIAgent _agent;
    private readonly MaiImageGeneratorDbContext _dbContext;
    private readonly IImageGenerator _imageGenerator;
    private readonly BlobContainerClient _container;

    public BookService(
        OpenAIClient client,
        IOptions<FoundryOptions> options,
        MaiImageGeneratorDbContext dbContext,
        IImageGenerator imageGenerator,
        BlobServiceClient blobServiceClient)
    {
        _agent = client.GetChatClient(options.Value.PromptModel).AsAIAgent(
            instructions: """
                You are a professional book editor and fiction writer. Follow the requested output format exactly,
                preserve the supplied plot and character continuity, and do not add Markdown or commentary unless asked.
                """,
            name: "BookWriter");
        _dbContext = dbContext;
        _imageGenerator = imageGenerator;
        _container = blobServiceClient.GetBlobContainerClient("generations");
    }

    public async Task<BookPlanResponse> GeneratePlanAsync(
        ProjectDocument project,
        int chapterCount,
        CancellationToken cancellationToken)
    {
        EnsureBook(project);
        if (chapterCount is < 1 or > MaximumChapterCount)
        {
            throw new ArgumentException($"A book plan requires between 1 and {MaximumChapterCount} chapters.");
        }

        project.BookPlan ??= new BookPlan();
        project.BookPlan.Status = GenerationStatus.Processing;
        project.BookPlan.Prompt = BuildPlanPrompt(project, chapterCount);
        project.BookPlan.Error = null;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var response = await _agent.RunAsync(project.BookPlan.Prompt, cancellationToken: cancellationToken);
            var generatedChapters = ParsePlan(response.ToString(), chapterCount);
            for (var index = 0; index < Math.Min(generatedChapters.Count, project.BookPlan.Chapters.Count); index++)
            {
                var generated = generatedChapters[index];
                var saved = project.BookPlan.Chapters[index];
                generated.Id = saved.Id;
                generated.ProsePrompt = saved.ProsePrompt;
                generated.Prose = saved.Prose;
                generated.ProseStatus = saved.ProseStatus;
                generated.ProseError = saved.ProseError;
                generated.ImagePrompt = saved.ImagePrompt;
                generated.ImageBlobName = saved.ImageBlobName;
                generated.ImageStatus = saved.ImageStatus;
                generated.ImageError = saved.ImageError;
            }

            project.BookPlan.Chapters = generatedChapters;
            project.BookPlan.Status = GenerationStatus.Completed;
            project.BookPlan.Error = null;
        }
        catch (OperationCanceledException)
        {
            project.BookPlan.Status = GenerationStatus.Pending;
            project.BookPlan.Error = "Book planning was interrupted and can be resumed by retrying.";
            await _dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            project.BookPlan.Status = GenerationStatus.Failed;
            project.BookPlan.Error = "The book plan could not be generated. Retry planning to try again.";
        }

        project.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(CancellationToken.None);
        return ToResponse(project);
    }

    public async Task<BookPlanResponse> UpdatePlanAsync(
        ProjectDocument project,
        UpdateBookPlanRequest request,
        CancellationToken cancellationToken)
    {
        EnsureBook(project);
        if (request.Chapters.Count is < 1 or > MaximumChapterCount ||
            request.Chapters.Any(chapter =>
                string.IsNullOrWhiteSpace(chapter.Title) ||
                string.IsNullOrWhiteSpace(chapter.Summary) ||
                string.IsNullOrWhiteSpace(chapter.CharacterEvents) ||
                string.IsNullOrWhiteSpace(chapter.Continuity)) ||
            request.Chapters.Select(chapter => chapter.Number).Distinct().Count() != request.Chapters.Count)
        {
            throw new ArgumentException("The plan requires uniquely ordered chapters with a title, summary, character events, and continuity.");
        }

        var existing = project.BookPlan?.Chapters.ToDictionary(chapter => chapter.Id, StringComparer.Ordinal) ?? [];
        var chapters = request.Chapters
            .OrderBy(chapter => chapter.Number)
            .Select((chapter, index) =>
            {
                existing.TryGetValue(chapter.Id, out var saved);
                return new BookChapter
                {
                    Id = saved?.Id ?? Guid.NewGuid().ToString("N"),
                    Number = index + 1,
                    Title = chapter.Title.Trim(),
                    Summary = chapter.Summary.Trim(),
                    CharacterEvents = chapter.CharacterEvents.Trim(),
                    Continuity = chapter.Continuity.Trim(),
                    ProsePrompt = saved?.ProsePrompt,
                    Prose = chapter.Prose?.Trim(),
                    ProseStatus = string.IsNullOrWhiteSpace(chapter.Prose)
                        ? GenerationStatus.Pending
                        : string.Equals(chapter.Prose.Trim(), saved?.Prose, StringComparison.Ordinal)
                            ? saved?.ProseStatus ?? GenerationStatus.Completed
                            : GenerationStatus.Completed,
                    ProseError = string.Equals(chapter.Prose?.Trim(), saved?.Prose, StringComparison.Ordinal)
                        ? saved?.ProseError
                        : null,
                    ImagePrompt = chapter.ImagePrompt?.Trim(),
                    ImageBlobName = saved?.ImageBlobName,
                    ImageStatus = saved?.ImageStatus ?? GenerationStatus.Pending,
                    ImageError = saved?.ImageError
                };
            })
            .ToList();

        project.BookPlan ??= new BookPlan();
        project.BookPlan.Chapters = chapters;
        project.BookPlan.Status = GenerationStatus.Completed;
        project.BookPlan.Error = null;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(project);
    }

    public BookPlanResponse GetPlan(ProjectDocument project)
    {
        EnsureBook(project);
        return ToResponse(project);
    }

    public async Task<BookPlanResponse> GenerateProseAsync(
        ProjectDocument project,
        string chapterId,
        CancellationToken cancellationToken)
    {
        var chapter = FindChapter(project, chapterId);
        chapter.ProseStatus = GenerationStatus.Processing;
        chapter.ProseError = null;
        chapter.ProsePrompt = BuildProsePrompt(project, chapter);
        await SaveAsync(project, cancellationToken);

        try
        {
            var response = await _agent.RunAsync(chapter.ProsePrompt, cancellationToken: cancellationToken);
            var prose = response.ToString().Trim();
            if (string.IsNullOrWhiteSpace(prose))
            {
                throw new JsonException("The prompt model returned no chapter prose.");
            }

            chapter.Prose = prose;
            chapter.ProseStatus = GenerationStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            chapter.ProseStatus = GenerationStatus.Pending;
            chapter.ProseError = "Chapter prose generation was interrupted and can be retried.";
            await SaveAsync(project, CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            chapter.ProseStatus = GenerationStatus.Failed;
            chapter.ProseError = "Chapter prose generation failed. This chapter can be retried independently.";
        }

        await SaveAsync(project, CancellationToken.None);
        return ToResponse(project);
    }

    public async Task<BookPlanResponse> GenerateImageAsync(
        ProjectDocument project,
        string chapterId,
        CancellationToken cancellationToken)
    {
        var chapter = FindChapter(project, chapterId);
        chapter.ImageStatus = GenerationStatus.Processing;
        chapter.ImageError = null;
        chapter.ImagePrompt = string.IsNullOrWhiteSpace(chapter.ImagePrompt)
            ? $"Book illustration, no text. {project.Context} Chapter: {chapter.Title}. {chapter.Summary} Continuity: {chapter.Continuity}"
            : chapter.ImagePrompt.Trim();
        await SaveAsync(project, cancellationToken);

        try
        {
            var image = await _imageGenerator.GenerateAsync(chapter.ImagePrompt, null, cancellationToken);
            var blobName = ArtifactPathBuilder.GetGenerationBlobName(
                project.OwnerId,
                Guid.Parse(project.Id),
                $"chapter-{chapter.Id}.png");
            await _container.GetBlobClient(blobName).UploadAsync(BinaryData.FromBytes(image), overwrite: true, cancellationToken);
            chapter.ImageBlobName = blobName;
            chapter.ImageStatus = GenerationStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            chapter.ImageStatus = GenerationStatus.Pending;
            chapter.ImageError = "Chapter image generation was interrupted and can be retried.";
            await SaveAsync(project, CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            chapter.ImageStatus = GenerationStatus.Failed;
            chapter.ImageError = "Chapter image generation failed. This image can be retried independently.";
        }

        await SaveAsync(project, CancellationToken.None);
        return ToResponse(project);
    }

    public async Task<Stream?> OpenImageAsync(
        ProjectDocument project,
        string chapterId,
        CancellationToken cancellationToken)
    {
        var chapter = FindChapter(project, chapterId);
        return chapter.ImageBlobName is null
            ? null
            : await _container.GetBlobClient(chapter.ImageBlobName).OpenReadAsync(cancellationToken: cancellationToken);
    }

    private async Task SaveAsync(ProjectDocument project, CancellationToken cancellationToken)
    {
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void EnsureBook(ProjectDocument project)
    {
        if (project.Type != ProjectType.Book)
        {
            throw new ArgumentException("The selected project is not a book.");
        }
    }

    private static BookChapter FindChapter(ProjectDocument project, string chapterId)
    {
        EnsureBook(project);
        return project.BookPlan?.Chapters.SingleOrDefault(chapter =>
                string.Equals(chapter.Id, chapterId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException("The chapter was not found.");
    }

    private static string BuildPlanPrompt(ProjectDocument project, int chapterCount) => $$"""
        Create a complete structured plan with exactly {{chapterCount}} chapters for this book.
        Source idea: {{project.SourceIdea}}
        Creative context: {{project.Context}}
        Return only JSON with this shape:
        {"chapters":[{"title":"...","summary":"...","characterEvents":"...","continuity":"..."}]}
        Every field must be detailed enough to guide later prose and illustration generation. Continuity must include
        relevant settings, plot points, character state, unresolved threads, and context needed to avoid contradictions.
        """;

    private static string BuildProsePrompt(ProjectDocument project, BookChapter chapter)
    {
        var plan = project.BookPlan!;
        var globalPlan = string.Join(
            "\n",
            plan.Chapters.Select(item =>
                $"{item.Number}. {item.Title}: {item.Summary} Character events: {item.CharacterEvents} Continuity: {item.Continuity}"));
        var previousContext = string.Join(
            "\n\n",
            plan.Chapters
                .Where(item => item.Number < chapter.Number && !string.IsNullOrWhiteSpace(item.Prose))
                .Select(item => $"Previous chapter {item.Number} ({item.Title}):\n{item.Prose}"));
        return $"""
            Write polished prose for chapter {chapter.Number}, "{chapter.Title}", of "{project.Title}".
            Source idea: {project.SourceIdea}
            Creative context: {project.Context}

            Global book plan:
            {globalPlan}

            Relevant generated continuity:
            {previousContext}

            Follow this chapter summary: {chapter.Summary}
            Include these character events and development: {chapter.CharacterEvents}
            Preserve this continuity: {chapter.Continuity}
            Return only the chapter prose.
            """;
    }

    private static List<BookChapter> ParsePlan(string content, int chapterCount)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new JsonException("The response did not contain a JSON object.");
        }

        var result = JsonSerializer.Deserialize<PlanResult>(content[start..(end + 1)], JsonSerializerOptions.Web);
        if (result?.Chapters is null ||
            result.Chapters.Count != chapterCount ||
            result.Chapters.Any(chapter =>
                string.IsNullOrWhiteSpace(chapter.Title) ||
                string.IsNullOrWhiteSpace(chapter.Summary) ||
                string.IsNullOrWhiteSpace(chapter.CharacterEvents) ||
                string.IsNullOrWhiteSpace(chapter.Continuity)))
        {
            throw new JsonException("The response did not contain a complete book plan.");
        }

        return result.Chapters.Select((chapter, index) => new BookChapter
        {
            Id = Guid.NewGuid().ToString("N"),
            Number = index + 1,
            Title = chapter.Title.Trim(),
            Summary = chapter.Summary.Trim(),
            CharacterEvents = chapter.CharacterEvents.Trim(),
            Continuity = chapter.Continuity.Trim(),
            ProseStatus = GenerationStatus.Pending,
            ImageStatus = GenerationStatus.Pending
        }).ToList();
    }

    private static BookPlanResponse ToResponse(ProjectDocument project)
    {
        var plan = project.BookPlan ?? new BookPlan { Status = GenerationStatus.Pending };
        return new(
            plan.Status,
            plan.Error,
            plan.Chapters.OrderBy(chapter => chapter.Number).Select(chapter => new BookChapterResponse(
                chapter.Id,
                chapter.Number,
                chapter.Title,
                chapter.Summary,
                chapter.CharacterEvents,
                chapter.Continuity,
                chapter.ProsePrompt,
                chapter.Prose,
                chapter.ProseStatus,
                chapter.ProseError,
                chapter.ImagePrompt,
                chapter.ImageBlobName is null
                    ? null
                    : new Uri($"api/projects/{project.Id}/book/chapters/{chapter.Id}/image", UriKind.Relative),
                chapter.ImageStatus,
                chapter.ImageError)).ToArray());
    }

    private sealed record PlanResult(IReadOnlyList<PlanChapter>? Chapters);
    private sealed record PlanChapter(string Title, string Summary, string CharacterEvents, string Continuity);
}
