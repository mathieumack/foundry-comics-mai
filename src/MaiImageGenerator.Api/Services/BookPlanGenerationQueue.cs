using Azure.Messaging.ServiceBus;
using MaiImageGenerator.Shared.Messages;

namespace MaiImageGenerator.Api.Services;

public sealed class BookPlanGenerationQueue(ServiceBusSender sender)
{
    public Task EnqueueAsync(
        BookPlanGenerationRequestMessage message,
        CancellationToken cancellationToken) =>
        sender.SendMessageAsync(
            new ServiceBusMessage(BinaryData.FromObjectAsJson(message))
            {
                MessageId = message.RequestId,
                ContentType = "application/json"
            },
            cancellationToken);
}
