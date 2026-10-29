using System.Text.Json;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Messages;
using MaiImageGenerator.Shared.Services;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace MaiImageGenerator.Jobs.BookPlanGeneration.Services;

public sealed class BookPlanGenerationService
{
    private const int MaximumChapterCount = 30;
    private readonly AIAgent _agent;
    private readonly MaiImageGeneratorDbContext _dbContext;

    public BookPlanGenerationService(
        OpenAIClient client,
        IOptions<FoundryOptions> options,
        MaiImageGeneratorDbContext dbContext)
    {
        _agent = client.GetChatClient(options.Value.PromptModel).AsAIAgent(
            instructions: """
                You are a professional book editor and fiction writer. Follow the requested output format exactly,
                preserve the supplied plot and character continuity, and do not add Markdown or commentary unless asked.
                """,
            name: "BookPlanGenerationJob");
        _dbContext = dbContext;
    }

    public async Task ProcessAsync(
        BookPlanGenerationRequestMessage request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var project = await _dbContext.Projects
            .WithPartitionKey(request.OwnerId)
            .SingleOrDefaultAsync(
                item => item.Id == request.ProjectId && item.OwnerId == request.OwnerId,
                cancellationToken)
            ?? throw new InvalidOperationException($"Project '{request.ProjectId}' was not found.");

        if (project.DeletionRequestedAt is not null)
        {
            return;
        }

        if (project.Type != ProjectType.Book)
        {
            throw new InvalidOperationException($"Project '{request.ProjectId}' is not a book.");
        }

        var plan = project.BookPlan
            ?? throw new InvalidOperationException($"Project '{request.ProjectId}' has no queued book plan.");
        if (BookImageGenerationLease.IsActive(project, DateTimeOffset.UtcNow))
        {
            throw new InvalidOperationException(
                $"Project '{request.ProjectId}' has an active chapter image batch.");
        }

        if (!string.Equals(plan.GenerationRequestId, request.RequestId, StringComparison.Ordinal) ||
            plan.Status == GenerationStatus.Completed)
        {
            return;
        }

        plan.Status = GenerationStatus.Processing;
        plan.Prompt = BuildPlanPrompt(project, request.ChapterCount);
        plan.Error = null;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var response = await _agent.RunAsync(plan.Prompt, cancellationToken: cancellationToken);
            var generatedChapters = ParsePlan(response.ToString(), request.ChapterCount);
            for (var index = 0; index < Math.Min(generatedChapters.Count, plan.Chapters.Count); index++)
            {
                PreserveChapterWork(generatedChapters[index], plan.Chapters[index]);
            }

            plan.Chapters = generatedChapters;
            plan.Status = GenerationStatus.Completed;
            plan.Error = null;
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            plan.Status = GenerationStatus.Failed;
            plan.Error = "The book plan could not be generated. Retry planning to try again.";
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private static void Validate(BookPlanGenerationRequestMessage request)
    {
        if (request.SchemaVersion != 1 ||
            !string.Equals(request.JobType, BookPlanGenerationRequestMessage.MessageType, StringComparison.Ordinal) ||
            !Guid.TryParse(request.ProjectId, out _) ||
            string.IsNullOrWhiteSpace(request.OwnerId) ||
            !Guid.TryParseExact(request.RequestId, "N", out _) ||
            request.ChapterCount is < 1 or > MaximumChapterCount)
        {
            throw new InvalidDataException("The book plan generation message is invalid.");
        }
    }

    private static string BuildPlanPrompt(ProjectDocument project, int chapterCount) => $$"""
        Create a complete structured plan with exactly {{chapterCount}} chapters for this book.
        Source idea: {{project.SourceIdea}}
        Creative context: {{project.Context}}
        Return only JSON with this shape:
        {"chapters":[{"title":"...","summary":"...","characterEvents":"...","continuity":"..."}]}
        All four chapter fields must be non-empty strings. Put detailed notes inside those strings;
        do not use nested objects or arrays for summary, characterEvents, or continuity.
        Every field must be detailed enough to guide later prose and illustration generation. Continuity must include
        relevant settings, plot points, character state, unresolved threads, and context needed to avoid contradictions.
        """;

    private static List<BookChapter> ParsePlan(string content, int chapterCount)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new JsonException("The response did not contain a JSON object.");
        }

        var result = JsonSerializer.Deserialize<PlanResult>(
            content[start..(end + 1)],
            JsonSerializerOptions.Web);
        if (result?.Chapters is null ||
            result.Chapters.Count != chapterCount ||
            result.Chapters.Any(chapter =>
                chapter is null || string.IsNullOrWhiteSpace(chapter.Title)))
        {
            throw new JsonException("The response did not contain a complete book plan.");
        }

        return result.Chapters.Select((chapter, index) => new BookChapter
        {
            Id = Guid.NewGuid().ToString("N"),
            Number = index + 1,
            Title = chapter.Title.Trim(),
            Summary = ReadPlanText(chapter.Summary),
            CharacterEvents = ReadPlanText(chapter.CharacterEvents),
            Continuity = ReadPlanText(chapter.Continuity),
            ProseStatus = GenerationStatus.Pending,
            ImageStatus = GenerationStatus.Pending
        }).ToList();
    }

    private static string ReadPlanText(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Object => string.Join(
                Environment.NewLine,
                value.EnumerateObject().Select(property => $"{property.Name}: {ReadPlanText(property.Value)}")),
            JsonValueKind.Array => string.Join(
                Environment.NewLine,
                value.EnumerateArray().Select(ReadPlanText)),
            _ => throw new JsonException("A book plan field must contain text, an object of notes, or a list of notes.")
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException("A book plan field must not be empty.");
        }

        return text;
    }

    private static void PreserveChapterWork(BookChapter generated, BookChapter saved)
    {
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

    private sealed record PlanResult(IReadOnlyList<PlanChapter>? Chapters);
    private sealed record PlanChapter(string Title, JsonElement Summary, JsonElement CharacterEvents, JsonElement Continuity);
}
