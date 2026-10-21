using System.Diagnostics;
using Dapr.Client;
using MaiImageGenerator.Shared.Messages;
using MaiImageGenerator.Shared.Telemetry;
using Microsoft.AspNetCore.Mvc;

namespace MaiImageGenerator.Api.Controllers;

[ApiController]
public sealed class PanelGenerationNotificationsController(
    DaprClient daprClient,
    ILogger<PanelGenerationNotificationsController> logger) : ControllerBase
{
    private const string SignalRBindingName = "signalr";

    [HttpPost(PanelGenerationNotifications.Started)]
    public async Task<IActionResult> Started(
        PanelGenerationStarted notification,
        CancellationToken cancellationToken)
    {
        if (!IsValid(notification.OwnerId, notification.BoardId, notification.PanelNumber))
        {
            return BadRequest();
        }

        await SendToUserAsync(
            notification.OwnerId,
            notification.BoardId,
            notification.PanelNumber,
            notification.TraceParent,
            nameof(PanelGenerationStarted),
            notification,
            cancellationToken);
        return Ok();
    }

    [HttpPost(PanelGenerationNotifications.Completed)]
    public async Task<IActionResult> Completed(
        PanelGenerationCompleted notification,
        CancellationToken cancellationToken)
    {
        if (!IsValid(notification.OwnerId, notification.BoardId, notification.PanelNumber))
        {
            return BadRequest();
        }

        await SendToUserAsync(
            notification.OwnerId,
            notification.BoardId,
            notification.PanelNumber,
            notification.TraceParent,
            nameof(PanelGenerationCompleted),
            notification,
            cancellationToken);
        return Ok();
    }

    private static bool IsValid(string ownerId, string boardId, int panelNumber) =>
        !string.IsNullOrWhiteSpace(ownerId) &&
        Guid.TryParse(boardId, out _) &&
        panelNumber > 0;

    private async Task SendToUserAsync(
        string ownerId,
        string boardId,
        int panelNumber,
        string? traceParent,
        string target,
        object notification,
        CancellationToken cancellationToken)
    {
        using var activity = OperationTelemetry.StartConsumerActivity("Deliver panel notification", traceParent);
        var generationId = Guid.Parse(boardId).ToString("D");
        using var scope = logger.BeginScope(
            "OperationId:{OperationId} GenerationId:{GenerationId} PanelNumber:{PanelNumber}",
            generationId,
            generationId,
            panelNumber);
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Async dependency {DependencyType} started at stage {GenerationStage} for notification {NotificationType}.",
            "DaprSignalR",
            "NotificationDelivery",
            target);
        var message = new
        {
            Target = target,
            Arguments = new[] { notification }
        };

        try
        {
            await daprClient.InvokeBindingAsync(
                SignalRBindingName,
                "create",
                message,
                new Dictionary<string, string> { ["user"] = ownerId },
                cancellationToken);
            logger.LogInformation(
                "Async dependency {DependencyType} completed with outcome {Outcome} for notification {NotificationType} in {ElapsedMs} ms.",
                "DaprSignalR",
                "Succeeded",
                target,
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(
                exception,
                "Async dependency {DependencyType} ended with outcome {Outcome} at stage {GenerationStage} after {ElapsedMs} ms.",
                "DaprSignalR",
                cancellationToken.IsCancellationRequested ? "Canceled" : "TimedOut",
                "NotificationDelivery",
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Async dependency {DependencyType} failed at stage {GenerationStage} after {ElapsedMs} ms.",
                "DaprSignalR",
                "NotificationDelivery",
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
