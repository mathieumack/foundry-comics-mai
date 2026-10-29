namespace MaiImageGenerator.Shared.Data.Entities;

public sealed class AiGenerationTrace
{
    public string Operation { get; set; } = string.Empty;
    public string Provider { get; set; } = "Microsoft Foundry";
    public string Model { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? TotalTokens { get; set; }
    public string UsageSource { get; set; } = "unavailable";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long? DurationMs { get; set; }
    public int? ImageCount { get; set; }
    public string? ImageSize { get; set; }
    public string? ImageQuality { get; set; }
    public string Status { get; set; } = "started";
}
