using Azure.Messaging.ServiceBus;
using MaiImageGenerator.Shared.Messages;

namespace MaiImageGenerator.Jobs.BoardGeneration.Services;

public sealed class PanelGenerationNotifier(
    ServiceBusSender startedQueue,
    ServiceBusSender completedQueue)
{
    public Task NotifyStartedAsync(
        string ownerId,
        string boardId,
        int panelNumber,
        CancellationToken cancellationToken,
        string? commandId = null) =>
        startedQueue.SendMessageAsync(
            CreateMessage(
                new PanelGenerationStarted(ownerId, boardId, panelNumber),
                $"{boardId}:{panelNumber}:{(commandId is null ? "" : commandId + ":")}started"),
            cancellationToken);

    public Task NotifyCompletedAsync(
        string ownerId,
        string boardId,
        int panelNumber,
        CancellationToken cancellationToken,
        string? commandId = null) =>
        completedQueue.SendMessageAsync(
            CreateMessage(
                new PanelGenerationCompleted(ownerId, boardId, panelNumber),
                $"{boardId}:{panelNumber}:{(commandId is null ? "" : commandId + ":")}completed"),
            cancellationToken);

    private static ServiceBusMessage CreateMessage<T>(T notification, string messageId) =>
        new(BinaryData.FromObjectAsJson(notification))
        {
            MessageId = messageId,
            ContentType = "application/json"
        };
}
