namespace MaiImageGenerator.Web.Models;

public sealed record AiGenerationTrace
{
    public string Operation { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string? RequestId { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? TotalTokens { get; init; }
    public string UsageSource { get; init; } = "unavailable";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public long? DurationMs { get; init; }
    public int? ImageCount { get; init; }
    public string? ImageSize { get; init; }
    public string? ImageQuality { get; init; }
    public string Status { get; init; } = string.Empty;
}
