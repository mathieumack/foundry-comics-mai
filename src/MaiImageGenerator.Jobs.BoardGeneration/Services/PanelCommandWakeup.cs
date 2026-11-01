using Azure.Messaging.ServiceBus;

namespace MaiImageGenerator.Jobs.BoardGeneration.Services;

public static class PanelCommandWakeup
{
    public static Task<long> ScheduleAsync(ServiceBusSender sender, ServiceBusReceivedMessage delivery,
        string commandId, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken)
    {
        var wakeup = new ServiceBusMessage(delivery)
        {
            // Business identity stays in the body. A new transport identity bypasses duplicate
            // detection for the wake-up, while duplicate scheduling for this expiry deduplicates.
            MessageId = $"{commandId}:wake:{leaseExpiresAt.UtcTicks}"
        };
        return sender.ScheduleMessageAsync(wakeup, leaseExpiresAt.AddSeconds(5), cancellationToken);
    }
}
