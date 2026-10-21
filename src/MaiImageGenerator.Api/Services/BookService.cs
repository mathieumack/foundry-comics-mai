using System.Text.Json;
using Azure.Storage.Blobs;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Api.Contracts;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Messages;
using MaiImageGenerator.Shared.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace MaiImageGenerator.Api.Services;

public sealed class BookService
{
    private const int MaximumChapterCount = 30;
    private static readonly TimeSpan ImageGenerationLeaseDuration = TimeSpan.FromMinutes(30);
    private readonly AIAgent _agent;
    private readonly MaiImageGeneratorDbContext _dbContext;
    private readonly IImageGenerator _imageGenerator;
    private readonly BlobContainerClient _container;
    private readonly BookPlanGenerationQueue _generationQueue;
    private readonly ImageGenerationCoordinator _imageGeneration;

    public BookService(
        OpenAIClient client,
        IOptions<FoundryOptions> options,
        MaiImageGeneratorDbContext dbContext,
        IImageGenerator imageGenerator,
        BlobServiceClient blobServiceClient,
        BookPlanGenerationQueue generationQueue,
        ImageGenerationCoordinator imageGeneration)
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
        _generationQueue = generationQueue;
        _imageGeneration = imageGeneration;
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

        var requestId = Guid.NewGuid().ToString("N");
        project.BookPlan ??= new BookPlan();
        project.BookPlan.GenerationRequestId = requestId;
        project.BookPlan.Status = GenerationStatus.Pending;
        project.BookPlan.Prompt = null;
        project.BookPlan.Error = null;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var message = new BookPlanGenerationRequestMessage
            {
                RequestId = requestId,
                ProjectId = project.Id,
                OwnerId = project.OwnerId,
                ChapterCount = chapterCount
            };
            await _generationQueue.EnqueueAsync(message, cancellationToken);
        }
        catch (Exception)
        {
            project.BookPlan.Status = GenerationStatus.Failed;
            project.BookPlan.Error = "The book plan could not be queued. Retry planning to try again.";
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }

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
        if (HasActiveImageGenerationLease(project, chapter))
        {
            return ToResponse(project);
        }

        return await GenerateImagesCoreAsync(project, [chapter], cancellationToken);
    }

    public async Task<BookPlanResponse> GenerateRemainingImagesAsync(
        ProjectDocument project,
        CancellationToken cancellationToken)
    {
        EnsureBook(project);
        var chapters = project.BookPlan?.Chapters
            .Where(chapter =>
                chapter.ImageStatus != GenerationStatus.Completed &&
                !HasActiveImageGenerationLease(project, chapter))
            .ToArray() ?? [];
        return await GenerateImagesCoreAsync(project, chapters, cancellationToken);
    }

    private async Task<BookPlanResponse> GenerateImagesCoreAsync(
        ProjectDocument project,
        IReadOnlyCollection<BookChapter> chapters,
        CancellationToken cancellationToken)
    {
        foreach (var chapter in chapters)
        {
            chapter.ImageStatus = GenerationStatus.Processing;
            chapter.ImageError = null;
            chapter.ImagePrompt = string.IsNullOrWhiteSpace(chapter.ImagePrompt)
                ? $"Book illustration, no text. {project.Context} Chapter: {chapter.Title}. {chapter.Summary} Continuity: {chapter.Continuity}"
                : chapter.ImagePrompt.Trim();
        }

        await SaveAsync(project, cancellationToken);
        using var persistenceLock = new SemaphoreSlim(1, 1);
        try
        {
            await _imageGeneration.RunAsync(
                chapters,
                "book-chapter",
                async (chapter, token) =>
                {
                    byte[]? image = null;
                    string? error = null;
                    var blobName = ArtifactPathBuilder.GetGenerationBlobName(
                        project.OwnerId,
                        Guid.Parse(project.Id),
                        $"chapter-{chapter.Id}.png");
                    try
                    {
                        image = await _imageGeneration.ExecuteWithRetryAsync(
                            "book-chapter",
                            retryToken => _imageGenerator.GenerateAsync(chapter.ImagePrompt!, null, retryToken),
                            IsTransient,
                            IsThrottled,
                            token);
                        await _container.GetBlobClient(blobName)
                            .UploadAsync(BinaryData.FromBytes(image), overwrite: true, token);
                    }
                    catch (Exception exception) when (
                        exception is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        error = exception.GetType().Name;
                    }

                    await persistenceLock.WaitAsync(CancellationToken.None);
                    try
                    {
                        if (image is not null && error is null)
                        {
                            chapter.ImageBlobName = blobName;
                            chapter.ImageStatus = GenerationStatus.Completed;
                            chapter.ImageError = null;
                        }
                        else
                        {
                            chapter.ImageStatus = GenerationStatus.Failed;
                            chapter.ImageError = "Chapter image generation failed. This image can be retried independently.";
                        }

                        await SaveAsync(project, CancellationToken.None);
                    }
                    finally
                    {
                        persistenceLock.Release();
                    }
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreach (var chapter in chapters.Where(chapter => chapter.ImageStatus == GenerationStatus.Processing))
            {
                chapter.ImageStatus = GenerationStatus.Pending;
                chapter.ImageError = "Chapter image generation was interrupted and can be retried.";
            }

            await SaveAsync(project, CancellationToken.None);
            throw;
        }

        return ToResponse(project);
    }

    private static bool IsTransient(Exception exception) =>
        exception is TimeoutException ||
        exception is OperationCanceledException ||
        exception is HttpRequestException { StatusCode: null } ||
        exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.RequestTimeout or
            System.Net.HttpStatusCode.TooManyRequests } ||
        exception is HttpRequestException { StatusCode: >= System.Net.HttpStatusCode.InternalServerError } ||
        exception is FoundryGenerationException { Status: 408 or 429 or >= 500 } ||
        exception is FoundryGenerationException { InnerException: HttpRequestException };

    private static bool IsThrottled(Exception exception) =>
        exception is FoundryGenerationException { Status: 429 } ||
        exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests };

    private static bool HasActiveImageGenerationLease(ProjectDocument project, BookChapter chapter) =>
        chapter.ImageStatus == GenerationStatus.Processing &&
        project.UpdatedAt > DateTimeOffset.UtcNow - ImageGenerationLeaseDuration;

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

    private static BookPlanResponse ToResponse(ProjectDocument project)
    {
        var plan = project.BookPlan ?? new BookPlan { Status = GenerationStatus.Pending };
        return new(
            plan.Status,
            project.BookPlan?.GenerationRequestId is not null &&
                plan.Status is GenerationStatus.Pending or GenerationStatus.Processing,
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

}
