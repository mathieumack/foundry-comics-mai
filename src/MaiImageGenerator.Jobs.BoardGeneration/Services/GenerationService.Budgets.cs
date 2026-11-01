using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Messages;
using MaiImageGenerator.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MaiImageGenerator.Jobs.BoardGeneration.Services;

public sealed partial class GenerationService
{
    private async Task ProcessBudgetedAsync(
        BoardGenerationRequestMessage request, GenerationDocument document, ProjectDocument project,
        CancellationToken cancellationToken)
    {
        document.Budget ??= CreateLegacyBudget(document);
        using var guard = new GenerationRunGuard(document,
            async token => { await dbContext.SaveChangesAsync(token); }, _commandTime, logger);
        // Selecting saved artwork only composes images; budget exhaustion must not undo approval.
        if (request.Operation == "compose")
        {
            await ProcessPanelCommandAsync(request, document, project, guard, cancellationToken);
            return;
        }
        var remaining = document.Budget.Deadline - _commandTime.GetUtcNow();
        using var timeout = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, _commandTime);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var previousAttemptCount = document.Attempts.Count;
        try
        {
            await guard.BeginAttemptAsync(cancellationToken);
            if (request.SchemaVersion == 4)
                await ProcessPanelCommandAsync(request, document, project, guard, deadline.Token);
            else
                await ProcessBoardAsync(request, document, project, guard, deadline.Token);
        }
        catch (GenerationBudgetCheckpointException)
        {
            // A write may have committed; never overwrite uncertain reservations with stale state.
            throw;
        }
        catch (Exception exception) when (exception is GenerationBudgetExceededException ||
            exception is OperationCanceledException && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var reason = (exception as GenerationBudgetExceededException)?.Reason ?? "deadline";
            document.Budget.StopReason ??= reason;
            document.Error = GenerationRunGuard.DescribeStop(document.Budget.StopReason);
            document.Status = GenerationStatus.Failed;
            document.PendingCommandId = null;
            document.PendingCommandKind = null;
            document.PendingPanelNumber = null;
            document.CommandExecutionId = null;
            document.CommandLeaseExpiresAt = null;
            var now = _commandTime.GetUtcNow();
            foreach (var attempt in document.Attempts.Where(item => item.CompletedAt is null))
            {
                attempt.Status = "stopped";
                attempt.CompletedAt = now;
            }
            if (document.Attempts.Count > previousAttemptCount)
                document.Attempts[^1].Status = "stopped";
            foreach (var stage in document.Budget.Stages.Where(item => item.Status is "processing" or "failed" or "unknown"))
            {
                stage.Status = "stopped";
                stage.StopReason = document.Budget.StopReason;
                stage.UpdatedAt = now;
            }
            foreach (var panel in document.Panels)
            {
                if (panel.Status is PanelGenerationStatus.Pending or PanelGenerationStatus.Processing)
                {
                    panel.Status = PanelGenerationStatus.Failed;
                    panel.Error = document.Error;
                }
                foreach (var version in panel.Versions.Where(item =>
                    item.Status is PanelGenerationStatus.Pending or PanelGenerationStatus.Processing))
                    version.Status = PanelGenerationStatus.Failed;
                foreach (var trace in panel.Traces.Concat(panel.Versions.SelectMany(item => item.Traces))
                    .Where(item => item.Status is "reserved" or "started" or "canceled"))
                    trace.StopReason = document.Budget.StopReason;
            }
            document.UpdatedAt = now;
            await dbContext.SaveChangesAsync(CancellationToken.None);
            logger.LogInformation("Generation run {RunId} stopped: {StopReason}; reserved calls {ReservedCalls}, attempts {Attempts}",
                document.Id, document.Budget.StopReason, document.Budget.ReservedModelCalls, document.Budget.ReservedAttempts);
        }
    }

    private GenerationRunBudget CreateLegacyBudget(GenerationDocument document)
    {
        var budget = (budgetOptions?.Value ?? new GenerationBudgetOptions()).Create(_commandTime.GetUtcNow());
        budget.ReservedAttempts = document.Attempts.Count;
        budget.HistoricalConsumptionKnown = false;
        // Historical traces are a lower bound, not a claim that pre-upgrade calls were all captured.
        foreach (var panel in document.Panels)
        {
            Import(panel.Traces, $"panel-{panel.Number}");
            foreach (var version in panel.Versions.Where(item => item.Id != panel.SelectedVersionId))
                Import(version.Traces, $"panel-{panel.Number}/version-{version.Id}");
        }
        return budget;

        void Import(IEnumerable<AiGenerationTrace> traces, string prefix)
        {
            foreach (var group in traces.GroupBy(item => item.Operation))
            {
                budget.ReservedModelCalls += group.Count();
                budget.Stages.Add(new GenerationStage
                {
                    Id = $"{prefix}/{group.Key}", Operation = group.Key,
                    ReservedCalls = group.Count(), Status = "unknown", UpdatedAt = _commandTime.GetUtcNow()
                });
            }
        }
    }
}
