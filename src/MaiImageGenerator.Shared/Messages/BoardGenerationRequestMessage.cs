namespace MaiImageGenerator.Shared.Messages;

public sealed record BoardGenerationRequestMessage
{
    public int SchemaVersion { get; init; } = 1;
    public string BoardId { get; init; } = string.Empty;
    public string OwnerId { get; init; } = string.Empty;
    public bool RewritePrompt { get; init; }
    public IReadOnlyList<BoardGenerationCharacter> Characters { get; init; } = [];
}

public sealed record BoardGenerationCharacter(
    string Id,
    string Name,
    IReadOnlyList<string> ReferenceIds);
