namespace MaiImageGenerator.Shared.Data.Entities;

public sealed class AiGenerationTrace
{
    public string Operation { get; set; } = string.Empty;
    public string Provider { get; set; } = "Microsoft Foundry";
    public string Model { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public string? AttemptId { get; set; }
    public string? RunId { get; set; }
    public string? StageId { get; set; }
    public int? InputVersion { get; set; }
    public int? RetryNumber { get; set; }
    public string? StopReason { get; set; }
    public string ProviderOutcome { get; set; } = "unknown";
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
