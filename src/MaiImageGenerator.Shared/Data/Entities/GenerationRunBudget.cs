namespace MaiImageGenerator.Shared.Data.Entities;

public sealed class GenerationRunBudget
{
    public int Version { get; set; } = 1;
    public int MaxModelCalls { get; set; }
    public int MaxAttempts { get; set; }
    public int MaxRetriesPerStage { get; set; }
    public DateTimeOffset Deadline { get; set; }
    // Reservations are never refunded: a lost response may still have incurred provider work.
    public int ReservedModelCalls { get; set; }
    public int ReservedAttempts { get; set; }
    public bool HistoricalConsumptionKnown { get; set; } = true;
    public string? StopReason { get; set; }
    public List<GenerationStage> Stages { get; set; } = [];
}

public sealed class GenerationStage
{
    public string Id { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string? AttemptId { get; set; }
    public int ReservedCalls { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset UpdatedAt { get; set; }
    public string? StopReason { get; set; }
}
