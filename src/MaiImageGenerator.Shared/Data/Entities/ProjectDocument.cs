namespace MaiImageGenerator.Shared.Data.Entities;

public sealed class ProjectDocument
{
    public string Id { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public int SchemaVersion { get; set; } = 1;
    public string Title { get; set; } = string.Empty;
    public string? Context { get; set; }
    public List<ProjectCharacter> Characters { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? ETag { get; set; }
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
