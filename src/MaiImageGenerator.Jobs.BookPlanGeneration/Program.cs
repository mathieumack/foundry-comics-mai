using Azure.Core;
using Azure.Messaging.ServiceBus;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Jobs.BoardGeneration.Hosting;
using MaiImageGenerator.Jobs.BookPlanGeneration.Services;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Messages;
using MDev.Dotnet.AspNetCore.Apis.Extensions;
using MDev.Dotnet.Azure.CosmosDb.Startup;
using MDev.Dotnet.Azure.StorageAccount.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using System.ClientModel.Primitives;

const int maximumDeliveryCount = 5;

var builder = Host.CreateApplicationBuilder(args);
var credential = builder.Services.GetManagedIdentity(builder.Configuration);

builder.RegisterConfiguration()
    .RegisterCosmosDb<MaiImageGeneratorDbContext>(credential)
    .RegisterAzureStorage(credential)
    .BindConfiguration<FoundryOptions>(FoundryOptions.SectionName);

builder.Services.AddLogging(builder.Configuration, "BookPlanGenerationJob");
builder.Services.AddSingleton<TokenCredential>(credential);
var serviceBusNamespace = builder.Configuration["ServiceBus:FullyQualifiedNamespace"]
    ?? throw new InvalidOperationException("Configuration 'ServiceBus:FullyQualifiedNamespace' is required.");
var serviceBusClient = new ServiceBusClient(serviceBusNamespace, credential);
builder.Services.AddSingleton(serviceBusClient);
builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<FoundryOptions>>().Value;
    var endpoint = new Uri($"{options.Endpoint.TrimEnd('/')}/openai/v1/");
    var authenticationPolicy = new BearerTokenPolicy(
        serviceProvider.GetRequiredService<TokenCredential>(),
        "https://ai.azure.com/.default");
#pragma warning disable OPENAI001 // Required for Entra ID token authentication with the OpenAI v1 endpoint.
    return new OpenAIClient(authenticationPolicy, new OpenAIClientOptions { Endpoint = endpoint });
#pragma warning restore OPENAI001
});
builder.Services.AddScoped<BookPlanGenerationService>();

using var host = builder.Build();

await host.StartAsync();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BookPlanGenerationJob");
var jobsQueueName = builder.Configuration["ServiceBus:JobsQueueName"] ?? "book-plan-jobs";
await using var jobsQueue = serviceBusClient.CreateReceiver(
    jobsQueueName,
    new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });

var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
var queueMessage = await jobsQueue.ReceiveMessageAsync(TimeSpan.FromSeconds(30), stopping);
if (queueMessage is null)
{
    logger.LogInformation("No book plan generation message is available.");
    await host.StopAsync();
    return 0;
}

using var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
var renewalTask = RenewMessageLockAsync(
    jobsQueue,
    queueMessage,
    processingCancellation,
    renewalCancellation.Token,
    logger);
try
{
    var request = queueMessage.Body.ToObjectFromJson<BookPlanGenerationRequestMessage>()
        ?? throw new InvalidDataException("The queue message body is empty.");
    using var scope = host.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<BookPlanGenerationService>()
        .ProcessAsync(request, processingCancellation.Token);
    renewalCancellation.Cancel();
    await ObserveRenewalAsync(renewalTask);
    await jobsQueue.CompleteMessageAsync(queueMessage, CancellationToken.None);
    logger.LogInformation(
        "Generated book plan for project {ProjectId} and owner {OwnerId}.",
        request.ProjectId,
        request.OwnerId);
    await host.StopAsync();
    return 0;
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested)
{
    renewalCancellation.Cancel();
    await ObserveRenewalAsync(renewalTask);
    await jobsQueue.AbandonMessageAsync(queueMessage, cancellationToken: CancellationToken.None);
    logger.LogInformation("Book plan generation was cancelled; message {MessageId} was released for retry.", queueMessage.MessageId);
    await host.StopAsync(CancellationToken.None);
    return 0;
}
catch (Exception exception)
{
    renewalCancellation.Cancel();
    await ObserveRenewalAsync(renewalTask);
    logger.LogError(
        exception,
        "Book plan generation failed for queue message {MessageId} on delivery {DeliveryCount}.",
        queueMessage.MessageId,
        queueMessage.DeliveryCount);
    if (queueMessage.DeliveryCount >= maximumDeliveryCount)
    {
        await jobsQueue.DeadLetterMessageAsync(
            queueMessage,
            "MaximumDeliveryCountExceeded",
            exception.Message,
            CancellationToken.None);
        logger.LogError(
            "Dead-lettered message {MessageId} after {DeliveryCount} deliveries.",
            queueMessage.MessageId,
            queueMessage.DeliveryCount);
    }
    else
    {
        await jobsQueue.AbandonMessageAsync(queueMessage, cancellationToken: CancellationToken.None);
    }

    await host.StopAsync(CancellationToken.None);
    return 1;
}

static async Task RenewMessageLockAsync(
    ServiceBusReceiver receiver,
    ServiceBusReceivedMessage message,
    CancellationTokenSource processingCancellation,
    CancellationToken renewalCancellation,
    ILogger logger)
{
    var delay = TimeSpan.FromMinutes(2);
    while (!renewalCancellation.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(delay, renewalCancellation);
            await receiver.RenewMessageLockAsync(message, renewalCancellation);
            delay = TimeSpan.FromMinutes(2);
        }
        catch (OperationCanceledException) when (renewalCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (ServiceBusException exception) when (
            exception.IsTransient &&
            message.LockedUntil > DateTimeOffset.UtcNow.AddSeconds(15))
        {
            logger.LogWarning(exception, "Transient failure renewing the lock for message {MessageId}.", message.MessageId);
            delay = TimeSpan.FromSeconds(5);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Lost the lock for message {MessageId}; cancelling generation.", message.MessageId);
            processingCancellation.Cancel();
            return;
        }
    }
}

static async Task ObserveRenewalAsync(Task renewalTask)
{
    try
    {
        await renewalTask;
    }
    catch (OperationCanceledException)
    {
    }
}
