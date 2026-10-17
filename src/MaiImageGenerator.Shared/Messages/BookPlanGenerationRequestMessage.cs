namespace MaiImageGenerator.Shared.Messages;

public sealed record BookPlanGenerationRequestMessage
{
    public const string MessageType = "book-plan";

    public int SchemaVersion { get; init; } = 1;
    public string JobType { get; init; } = MessageType;
    public string RequestId { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    public string OwnerId { get; init; } = string.Empty;
    public int ChapterCount { get; init; }
}
