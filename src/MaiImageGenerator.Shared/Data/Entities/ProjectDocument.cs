namespace MaiImageGenerator.Shared.Data.Entities;

public sealed class ProjectDocument
{
    public string Id { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public int SchemaVersion { get; set; } = 1;
    public ProjectType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Context { get; set; }
    public string? SourceIdea { get; set; }
    public BookPlan? BookPlan { get; set; }
    public List<ProjectCharacter> Characters { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? ETag { get; set; }
}

public enum ProjectType
{
    Comic,
    Book
}

public sealed class BookPlan
{
    public string? GenerationRequestId { get; set; }
    public string? Prompt { get; set; }
    public GenerationStatus Status { get; set; }
    public string? Error { get; set; }
    public List<BookChapter> Chapters { get; set; } = [];
}

public sealed class BookChapter
{
    public string Id { get; set; } = string.Empty;
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string CharacterEvents { get; set; } = string.Empty;
    public string Continuity { get; set; } = string.Empty;
    public string? ProsePrompt { get; set; }
    public string? Prose { get; set; }
    public GenerationStatus ProseStatus { get; set; }
    public string? ProseError { get; set; }
    public string? ImagePrompt { get; set; }
    public string? ImageBlobName { get; set; }
    public GenerationStatus ImageStatus { get; set; }
    public string? ImageError { get; set; }
    public string? ImageGenerationRequestId { get; set; }
    public DateTimeOffset? ImageGenerationStartedAt { get; set; }
}

public sealed class ProjectCharacter
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<ProjectCharacterReference> References { get; set; } = [];
}

public sealed class ProjectCharacterReference
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
}
