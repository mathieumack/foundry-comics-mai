using MaiImageGenerator.Shared.Data.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace MaiImageGenerator.Shared.Services;

public sealed class GenerationRunGuard(
    GenerationDocument document,
    Func<CancellationToken, Task> checkpoint,
    TimeProvider timeProvider,
    ILogger? logger = null) : IDisposable
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public Action? Fence { get; set; }
    public GenerationRunBudget Budget => document.Budget
        ?? throw new InvalidOperationException("The run budget has not been initialized.");

    public async Task BeginAttemptAsync(CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            CheckLimits();
            if (Budget.ReservedAttempts >= Budget.MaxAttempts)
                Stop("attempt-limit");
            Budget.ReservedAttempts++;
            await SaveAsync(token);
        }
        catch (GenerationBudgetExceededException)
        {
            await SaveAsync(token);
            throw;
        }
        finally { Gate.Release(); }
    }

    public async Task<T> InvokeAsync<T>(
        string stageId, string operation, string attemptId, int maxRetries,
        List<AiGenerationTrace> traces, Func<AiGenerationTrace, CancellationToken, Task<T>> invoke,
        CancellationToken token)
    {
        AiGenerationTrace trace;
        GenerationStage stage;
        await Gate.WaitAsync(token);
        try
        {
            CheckLimits();
            if (Budget.ReservedModelCalls >= Budget.MaxModelCalls)
                Stop("call-limit");
            stage = Budget.Stages.SingleOrDefault(item => item.Id == stageId)!;
            if (stage is null)
            {
                stage = new GenerationStage { Id = stageId, Operation = operation };
                Budget.Stages.Add(stage);
            }
            if (stage.ReservedCalls >= 1 + Math.Min(maxRetries, Budget.MaxRetriesPerStage))
                Stop("retry-limit");
            trace = new AiGenerationTrace
            {
                Operation = operation,
                RunId = document.Id,
                StageId = stageId,
                AttemptId = attemptId,
                InputVersion = document.InputSnapshot?.Version,
                RetryNumber = stage.ReservedCalls,
                StartedAt = timeProvider.GetUtcNow(),
                Status = "reserved"
            };
            traces.Add(trace);
            Budget.ReservedModelCalls++;
            stage.ReservedCalls++;
            stage.AttemptId = attemptId;
            stage.Status = "processing";
            stage.UpdatedAt = timeProvider.GetUtcNow();
            await SaveAsync(token);
            Log(stage);
        }
        catch (GenerationBudgetExceededException)
        {
            await SaveAsync(token);
            throw;
        }
        finally { Gate.Release(); }

        try
        {
            token.ThrowIfCancellationRequested();
            // Check again after persistence: a slow checkpoint must not authorize work past the deadline.
            if (timeProvider.GetUtcNow() >= Budget.Deadline)
                throw new GenerationBudgetExceededException("deadline");
            return await invoke(trace, token);
        }
        catch (GenerationBudgetExceededException exception)
        {
            Budget.StopReason ??= exception.Reason;
            trace.StopReason = Budget.StopReason;
            throw;
        }
        finally
        {
            await Gate.WaitAsync(CancellationToken.None);
            try
            {
                stage.Status = trace.Status is "completed" ? "completed" : "failed";
                if (trace.ProviderOutcome == "unknown")
                    stage.Status = "unknown";
                stage.UpdatedAt = timeProvider.GetUtcNow();
                await SaveAsync(CancellationToken.None);
                Log(stage);
            }
            finally { Gate.Release(); }
        }
    }

    public async Task<T> StageAsync<T>(string stageId, string operation, string attemptId,
        Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        GenerationStage stage;
        await Gate.WaitAsync(token);
        try
        {
            stage = Budget.Stages.SingleOrDefault(item => item.Id == stageId)
                ?? new GenerationStage { Id = stageId, Operation = operation };
            if (!Budget.Stages.Contains(stage))
                Budget.Stages.Add(stage);
            stage.AttemptId = attemptId;
            stage.Status = "processing";
            stage.UpdatedAt = timeProvider.GetUtcNow();
            await SaveAsync(token);
            Log(stage);
        }
        finally { Gate.Release(); }
        var status = "failed";
        try
        {
            var result = await action(token);
            status = "completed";
            return result;
        }
        finally
        {
            await Gate.WaitAsync(CancellationToken.None);
            try
            {
                stage.Status = status;
                stage.UpdatedAt = timeProvider.GetUtcNow();
                await SaveAsync(CancellationToken.None);
                Log(stage);
            }
            finally { Gate.Release(); }
        }
    }

    private void Log(GenerationStage stage) =>
        logger?.LogInformation(
            "Generation run {RunId} stage {StageId} operation {Operation} attempt {AttemptId} input version {InputVersion}: {Status}; reserved calls {ReservedCalls}, stop {StopReason}",
            document.Id, stage.Id, stage.Operation, stage.AttemptId, document.InputSnapshot?.Version,
            stage.Status, Budget.ReservedModelCalls, Budget.StopReason);

    private void CheckLimits()
    {
        if (Budget.Version != 1)
            throw new InvalidDataException("The run budget version is unsupported.");
        if (Budget.StopReason is not null)
            throw new GenerationBudgetExceededException(Budget.StopReason);
        if (timeProvider.GetUtcNow() >= Budget.Deadline)
            Stop("deadline");
    }

    private void Stop(string reason)
    {
        Budget.StopReason ??= reason;
        throw new GenerationBudgetExceededException(Budget.StopReason);
    }

    private async Task SaveAsync(CancellationToken token)
    {
        Fence?.Invoke();
        try { await checkpoint(token); }
        catch (DbUpdateConcurrencyException) { throw; }
        catch (Exception exception) { throw new GenerationBudgetCheckpointException(exception); }
    }

    public static string DescribeStop(string reason) => reason switch
    {
        "call-limit" => "Generation stopped because the run's model-call limit was reached.",
        "retry-limit" => "Generation stopped because a stage's retry limit was reached.",
        "attempt-limit" => "Generation stopped because the run's execution-attempt limit was reached.",
        "deadline" => "Generation stopped because the run's time limit was reached.",
        _ => "Generation stopped because its run budget is unavailable."
    };

    public void Dispose() => Gate.Dispose();
}

public sealed class GenerationBudgetExceededException(string reason)
    : Exception(GenerationRunGuard.DescribeStop(reason))
{
    public string Reason { get; } = reason;
}

public sealed class GenerationBudgetCheckpointException(Exception innerException)
    : Exception("The run budget checkpoint could not be confirmed.", innerException);
