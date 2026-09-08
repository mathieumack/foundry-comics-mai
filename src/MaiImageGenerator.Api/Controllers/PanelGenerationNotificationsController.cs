using Dapr.Client;
using MaiImageGenerator.Shared.Messages;
using Microsoft.AspNetCore.Mvc;

namespace MaiImageGenerator.Api.Controllers;

[ApiController]
public sealed class PanelGenerationNotificationsController(
    DaprClient daprClient) : ControllerBase
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
            nameof(PanelGenerationCompleted),
            notification,
            cancellationToken);
        return Ok();
    }

    private static bool IsValid(string ownerId, string boardId, int panelNumber) =>
        !string.IsNullOrWhiteSpace(ownerId) &&
        Guid.TryParse(boardId, out _) &&
        panelNumber > 0;

    private Task SendToUserAsync(
        string ownerId,
        string target,
        object notification,
        CancellationToken cancellationToken)
    {
        var message = new
        {
            Target = target,
            Arguments = new[] { notification }
        };

        return daprClient.InvokeBindingAsync(
            SignalRBindingName,
            "create",
            message,
            new Dictionary<string, string> { ["user"] = ownerId },
            cancellationToken);
    }
}
