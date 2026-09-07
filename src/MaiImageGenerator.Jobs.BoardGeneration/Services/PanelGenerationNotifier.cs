using Azure.Storage.Queues;
using MaiImageGenerator.Shared.Messages;

namespace MaiImageGenerator.Jobs.BoardGeneration.Services;

public sealed class PanelGenerationNotifier(
    QueueClient startedQueue,
    QueueClient completedQueue)
{
    public Task NotifyStartedAsync(
        string ownerId,
        string boardId,
        int panelNumber,
        CancellationToken cancellationToken) =>
        startedQueue.SendMessageAsync(
            BinaryData.FromObjectAsJson(new PanelGenerationStarted(ownerId, boardId, panelNumber)).ToString(),
            cancellationToken);

    public Task NotifyCompletedAsync(
        string ownerId,
        string boardId,
        int panelNumber,
        CancellationToken cancellationToken) =>
        completedQueue.SendMessageAsync(
            BinaryData.FromObjectAsJson(new PanelGenerationCompleted(ownerId, boardId, panelNumber)).ToString(),
            cancellationToken);
}
