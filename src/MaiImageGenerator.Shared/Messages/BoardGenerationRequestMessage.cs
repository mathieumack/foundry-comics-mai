namespace MaiImageGenerator.Shared.Messages;

public sealed record BoardGenerationRequestMessage
{
    public int SchemaVersion { get; init; } = 2;
    public string BoardId { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    public string OwnerId { get; init; } = string.Empty;
    public bool RewritePrompt { get; init; }
}
