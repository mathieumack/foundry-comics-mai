using Azure.Core;
using Azure.Messaging.ServiceBus;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Jobs.BoardGeneration.Hosting;
using MaiImageGenerator.Jobs.BoardGeneration.Services;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Messages;
using MaiImageGenerator.Shared.Services;
using MDev.Dotnet.AspNetCore.Apis.Extensions;
using MDev.Dotnet.Azure.CosmosDb.Startup;
using MDev.Dotnet.Azure.StorageAccount.Startup;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Images;
using System.ClientModel.Primitives;

const int maximumDeliveryCount = 5;

var builder = Host.CreateApplicationBuilder(args);

// Managed Identity credential (uses AZURE_CLIENT_ID env var for user-assigned identity)
var credential = builder.Services.GetManagedIdentity(builder.Configuration);

// Register configuration
var customMeters = new List<string>
{
    ImageGenerationCoordinator.MeterName
};

builder.RegisterConfiguration()
        .RegisterCosmosDb<MaiImageGeneratorDbContext>(credential)
        .RegisterAzureStorage(credential)
        .BindConfiguration<FoundryOptions>(FoundryOptions.SectionName);

builder.Services.AddLogging(builder.Configuration, "BoardGenerationJob");

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
builder.Services.AddSingleton<AIAgent>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<FoundryOptions>>().Value;
    return serviceProvider.GetRequiredService<OpenAIClient>()
        .GetChatClient(options.PromptModel)
        .AsAIAgent(
            instructions: FoundryImageClient.RewriteInstructions,
            name: "BoardGenerationJob");
});
builder.Services.AddSingleton<ImageClient>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<FoundryOptions>>().Value;
    var authenticationPolicy = new BearerTokenPolicy(
        serviceProvider.GetRequiredService<TokenCredential>(),
        "https://cognitiveservices.azure.com/.default");
#pragma warning disable OPENAI001 // Required for Entra ID token authentication with the MAI v1 endpoint.
    var client = new OpenAIClient(authenticationPolicy, new OpenAIClientOptions
    {
        Endpoint = GetMaiImageEndpoint(options.Endpoint)
    });
#pragma warning restore OPENAI001
    return client.GetImageClient(options.ImageModel);
});
builder.Services.AddHttpClient<FoundryImageClient>(client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddSingleton(serviceProvider =>
    new ImageGenerationCoordinator(serviceProvider.GetRequiredService<IOptions<FoundryOptions>>().Value));

builder.Services.AddSingleton(new PanelGenerationNotifier(
    serviceBusClient.CreateSender(PanelGenerationNotifications.Started),
    serviceBusClient.CreateSender(PanelGenerationNotifications.Completed)));
builder.Services.AddScoped<ReferenceService>();
builder.Services.AddScoped<ImageComposer>();
builder.Services.AddScoped<GenerationService>();

using var host = builder.Build();

await host.StartAsync();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BoardGenerationJob");

var jobsQueueName = builder.Configuration["ServiceBus:JobsQueueName"] ?? "generation-jobs";
await using var jobsQueue = serviceBusClient.CreateReceiver(
    jobsQueueName,
    new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });

var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
var queueMessage = await jobsQueue.ReceiveMessageAsync(TimeSpan.FromSeconds(30), stopping);
if (queueMessage is null)
{
    logger.LogInformation("No board generation message is available.");
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
    var request = queueMessage.Body.ToObjectFromJson<BoardGenerationRequestMessage>()
        ?? throw new InvalidDataException("The queue message body is empty.");
    using var scope = host.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<GenerationService>()
        .ProcessAsync(request, processingCancellation.Token);
    renewalCancellation.Cancel();
    await ObserveRenewalAsync(renewalTask);
    await jobsQueue.CompleteMessageAsync(queueMessage, CancellationToken.None);
    logger.LogInformation("Generated board {BoardId} for owner {OwnerId}.", request.BoardId, request.OwnerId);
    await host.StopAsync();
    return 0;
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested)
{
    renewalCancellation.Cancel();
    await ObserveRenewalAsync(renewalTask);
    await jobsQueue.AbandonMessageAsync(queueMessage, cancellationToken: CancellationToken.None);
    logger.LogInformation("Board generation was cancelled; message {MessageId} was released for retry.", queueMessage.MessageId);
    await host.StopAsync(CancellationToken.None);
    return 0;
}
catch (Exception exception)
{
    renewalCancellation.Cancel();
    await ObserveRenewalAsync(renewalTask);
    logger.LogError(exception, "Board generation failed for queue message {MessageId} on delivery {DeliveryCount}.",
        queueMessage.MessageId, queueMessage.DeliveryCount);
    if (queueMessage.DeliveryCount >= maximumDeliveryCount)
    {
        await jobsQueue.DeadLetterMessageAsync(
            queueMessage,
            "MaximumDeliveryCountExceeded",
            exception.Message,
            CancellationToken.None);
        logger.LogError("Dead-lettered message {MessageId} after {DeliveryCount} deliveries.",
            queueMessage.MessageId, queueMessage.DeliveryCount);
    }
    else
    {
        await jobsQueue.AbandonMessageAsync(queueMessage, cancellationToken: CancellationToken.None);
    }

    await host.StopAsync(CancellationToken.None);
    return 1;
}

static Uri GetMaiImageEndpoint(string endpoint)
{
    var builder = new UriBuilder(endpoint);
    const string cognitiveServicesSuffix = ".cognitiveservices.azure.com";
    if (builder.Host.EndsWith(cognitiveServicesSuffix, StringComparison.OrdinalIgnoreCase))
    {
        builder.Host = $"{builder.Host[..^cognitiveServicesSuffix.Length]}.services.ai.azure.com";
    }

    builder.Path = "/mai/v1";
    builder.Query = string.Empty;
    return builder.Uri;
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