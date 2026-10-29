using System.Text.Json;
using Azure.Storage.Blobs;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Api.Contracts;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Generation;
using MaiImageGenerator.Shared.Messages;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace MaiImageGenerator.Api.Services;

public sealed class BookService
{
    private const int MaximumChapterCount = 30;
    private const string Instructions = """
        You are a professional book editor and fiction writer. Follow the requested output format exactly,
        preserve the supplied plot and character continuity, and do not add Markdown or commentary unless asked.
        """;
    private readonly ChatClient _client;
    private readonly MaiImageGeneratorDbContext _dbContext;
    private readonly IImageGenerator _imageGenerator;
    private readonly BlobContainerClient _container;
    private readonly BookPlanGenerationQueue _generationQueue;
    private readonly ILogger<BookService> _logger;
    private readonly string _promptModel;
    private readonly string _imageModel;

    public BookService(
        OpenAIClient client,
        IOptions<FoundryOptions> options,
        MaiImageGeneratorDbContext dbContext,
        IImageGenerator imageGenerator,
        BlobServiceClient blobServiceClient,
        BookPlanGenerationQueue generationQueue,
        ILogger<BookService> logger)
    {
        _client = client.GetChatClient(options.Value.PromptModel);
        _dbContext = dbContext;
        _imageGenerator = imageGenerator;
        _container = blobServiceClient.GetBlobContainerClient("generations");
        _generationQueue = generationQueue;
        _logger = logger;
        _promptModel = options.Value.PromptModel;
        _imageModel = options.Value.ImageModel;
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
        project.SchemaVersion = 3;
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
                    ImageError = saved?.ImageError,
                    ProseTraces = saved?.ProseTraces.Select(AiTraceCapture.Clone).ToList() ?? [],
                    ImageTraces = saved?.ImageTraces.Select(AiTraceCapture.Clone).ToList() ?? []
                };
            })
            .ToList();

        project.BookPlan ??= new BookPlan();
        project.BookPlan.Chapters = chapters;
        project.BookPlan.Status = GenerationStatus.Completed;
        project.BookPlan.Error = null;
        project.SchemaVersion = 3;
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
        var trace = new AiGenerationTrace
        {
            Operation = "chapter-prose", Model = _promptModel, StartedAt = DateTimeOffset.UtcNow
        };
        chapter.ProseTraces.Add(trace);
        await SaveAsync(project, cancellationToken);

        try
        {
            using var capture = AiTraceCapture.Begin(trace, "chapter-prose", _promptModel);
            try
            {
                var agent = new FoundryChatTraceClient(_client, trace).AsAIAgent(
                    instructions: Instructions, name: "BookWriter");
                var response = await agent.RunAsync(chapter.ProsePrompt, cancellationToken: cancellationToken);
                var prose = response.ToString().Trim();
                if (string.IsNullOrWhiteSpace(prose))
                {
                    throw new JsonException("The prompt model returned no chapter prose.");
                }

                chapter.Prose = prose;
                chapter.ProseStatus = GenerationStatus.Completed;
                capture.Complete();
            }
            catch (OperationCanceledException)
            {
                capture.Cancel();
                throw;
            }
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
        var trace = new AiGenerationTrace
        {
            Operation = "chapter-image", Model = _imageModel, StartedAt = DateTimeOffset.UtcNow
        };
        chapter.ImageTraces.Add(trace);
        await SaveAsync(project, cancellationToken);

        try
        {
            var image = await _imageGenerator.GenerateAsync(chapter.ImagePrompt, null, cancellationToken, trace);
            var blobName = ArtifactPathBuilder.GetGenerationBlobName(
                project.OwnerId,
                Guid.Parse(project.Id),
                $"chapter-{chapter.Id}.png");
            await _container.GetBlobClient(blobName).UploadAsync(BinaryData.FromBytes(image), overwrite: true, cancellationToken);
            chapter.ImageBlobName = blobName;
            chapter.ImageStatus = GenerationStatus.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            chapter.ImageStatus = GenerationStatus.Pending;
            chapter.ImageError = "Chapter image generation was interrupted and can be retried.";
            await SaveAsync(project, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            chapter.ImageStatus = GenerationStatus.Failed;
            chapter.ImageError = "Chapter image generation failed. This image can be retried independently.";
            await SaveAsync(project, CancellationToken.None);
            _logger.LogError(
                "Chapter image generation failed for project {ProjectId}, chapter {ChapterId}; failure type {FailureType}, upstream HTTP status {UpstreamStatusCode}.",
                project.Id,
                chapter.Id,
                exception.GetType().Name,
                (exception as FoundryGenerationException)?.UpstreamStatusCode);
            throw;
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
        project.SchemaVersion = 3;
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

    public static BookPlanResponse ToResponse(ProjectDocument project)
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
                chapter.ImageError,
                chapter.ProseTraces.ToArray(),
                chapter.ImageTraces.ToArray())).ToArray(),
            plan.Traces.ToArray());
    }

}
