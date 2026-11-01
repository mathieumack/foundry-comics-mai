using MaiImageGenerator.Shared.Data.Entities;

namespace MaiImageGenerator.Shared.Services;

public sealed class GenerationBudgetOptions
{
    public const string SectionName = "GenerationBudget";
    public int MaxModelCalls { get; set; } = 64;
    public int MaxAttempts { get; set; } = 32;
    public int MaxRetriesPerStage { get; set; } = 3;
    public int MaxDurationSeconds { get; set; } = 1800;

    public GenerationRunBudget Create(DateTimeOffset now)
    {
        if (MaxModelCalls is < 1 or > 1000 || MaxAttempts is < 1 or > 100 ||
            MaxRetriesPerStage is < 0 or > 10 || MaxDurationSeconds is < 1 or > 86400)
            throw new InvalidOperationException("The generation budget configuration is outside its supported limits.");
        return new GenerationRunBudget
        {
            MaxModelCalls = MaxModelCalls,
            MaxAttempts = MaxAttempts,
            MaxRetriesPerStage = MaxRetriesPerStage,
            Deadline = now.AddSeconds(MaxDurationSeconds)
        };
    }
}
