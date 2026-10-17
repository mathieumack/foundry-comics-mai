using Azure.Storage.Queues;
using MaiImageGenerator.Shared.Messages;

namespace MaiImageGenerator.Api.Services;

public sealed class BookPlanGenerationQueue(QueueClient queueClient)
{
    public Task EnqueueAsync(
        BookPlanGenerationRequestMessage message,
        CancellationToken cancellationToken) =>
        queueClient.SendMessageAsync(
            BinaryData.FromObjectAsJson(message).ToString(),
            cancellationToken);
}
