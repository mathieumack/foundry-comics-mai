using System.Diagnostics;
using Azure.Storage.Queues;
using MaiImageGenerator.Shared.Messages;
using MaiImageGenerator.Shared.Telemetry;
using Microsoft.Extensions.Logging;

namespace MaiImageGenerator.Jobs.BoardGeneration.Services;

public sealed class PanelGenerationNotifier(
    QueueClient startedQueue,
    QueueClient completedQueue,
    ILogger<PanelGenerationNotifier> logger)
{
    public Task NotifyStartedAsync(
        string ownerId,
        string boardId,
        int panelNumber,
        CancellationToken cancellationToken) =>
        NotifyAsync(
            startedQueue,
            new PanelGenerationStarted(
                ownerId,
                boardId,
                panelNumber,
                OperationTelemetry.CurrentTraceParent),
            boardId,
            panelNumber,
            "Started",
            cancellationToken);

    public Task NotifyCompletedAsync(
        string ownerId,
        string boardId,
        int panelNumber,
        CancellationToken cancellationToken) =>
        NotifyAsync(
            completedQueue,
            new PanelGenerationCompleted(
                ownerId,
                boardId,
                panelNumber,
                OperationTelemetry.CurrentTraceParent),
            boardId,
            panelNumber,
            "Completed",
            cancellationToken);

    private async Task NotifyAsync(
        QueueClient queue,
        object notification,
        string boardId,
        int panelNumber,
        string notificationType,
        CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(
            "OperationId:{OperationId} GenerationId:{GenerationId} PanelNumber:{PanelNumber}",
            boardId,
            boardId,
            panelNumber);
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Async dependency {DependencyType} started at stage {GenerationStage} for notification {NotificationType}.",
            "Queue",
            "PanelNotification",
            notificationType);
        try
        {
            await queue.SendMessageAsync(
                BinaryData.FromObjectAsJson(notification).ToString(),
                cancellationToken);
            logger.LogInformation(
                "Async dependency {DependencyType} completed with outcome {Outcome} for notification {NotificationType} in {ElapsedMs} ms.",
                "Queue",
                "Succeeded",
                notificationType,
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(
                exception,
                "Async dependency {DependencyType} ended with outcome {Outcome} at stage {GenerationStage} after {ElapsedMs} ms.",
                "Queue",
                cancellationToken.IsCancellationRequested ? "Canceled" : "TimedOut",
                "PanelNotification",
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Async dependency {DependencyType} failed at stage {GenerationStage} after {ElapsedMs} ms.",
                "Queue",
                "PanelNotification",
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
