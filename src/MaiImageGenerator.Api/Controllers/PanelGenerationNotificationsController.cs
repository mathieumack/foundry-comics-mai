using MaiImageGenerator.Api.Hubs;
using MaiImageGenerator.Shared.Messages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace MaiImageGenerator.Api.Controllers;

[ApiController]
public sealed class PanelGenerationNotificationsController(
    IHubContext<NotificationHub> hubContext) : ControllerBase
{
    [HttpPost(PanelGenerationNotifications.Started)]
    public async Task<IActionResult> Started(
        PanelGenerationStarted notification,
        CancellationToken cancellationToken)
    {
        if (!IsValid(notification.OwnerId, notification.BoardId, notification.PanelNumber))
        {
            return BadRequest();
        }

        await hubContext.Clients.User(notification.OwnerId).SendAsync(
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

        await hubContext.Clients.User(notification.OwnerId).SendAsync(
            nameof(PanelGenerationCompleted),
            notification,
            cancellationToken);
        return Ok();
    }

    private static bool IsValid(string ownerId, string boardId, int panelNumber) =>
        !string.IsNullOrWhiteSpace(ownerId) &&
        Guid.TryParse(boardId, out _) &&
        panelNumber > 0;
}
