using Azure.Core;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Jobs.BoardGeneration.Hosting;
using MaiImageGenerator.Jobs.BookPlanGeneration.Services;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Messages;
using MaiImageGenerator.Shared.Telemetry;
using MDev.Dotnet.AspNetCore.Apis.Extensions;
using MDev.Dotnet.Azure.CosmosDb.Startup;
using MDev.Dotnet.Azure.StorageAccount.Settings;
using MDev.Dotnet.Azure.StorageAccount.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using System.Diagnostics;
using System.ClientModel.Primitives;

const int maximumDequeueCount = 5;

var builder = Host.CreateApplicationBuilder(args);
var credential = builder.Services.GetManagedIdentity(builder.Configuration);

builder.RegisterConfiguration()
    .RegisterCosmosDb<MaiImageGeneratorDbContext>(credential)
    .RegisterAzureStorage(credential)
    .BindConfiguration<FoundryOptions>(FoundryOptions.SectionName)
    .BindConfiguration(out StorageAccountSettings storageSettings, StorageAccountSettings.SectionName);

builder.Services.AddLogging(builder.Configuration, "BookPlanGenerationJob");
builder.Services.AddSingleton<TokenCredential>(credential);
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
var queueClientOptions = new QueueClientOptions
{
    MessageEncoding = storageSettings.QueueMessagesEncodeBase64
        ? QueueMessageEncoding.Base64
        : QueueMessageEncoding.None
};
var jobsQueueName = builder.Configuration["Storage:JobsQueueName"] ?? "book-plan-jobs";
var jobsQueue = new QueueClient(
    new Uri($"{storageSettings.QueuesEndpoint.TrimEnd('/')}/{jobsQueueName}"),
    credential,
    queueClientOptions);

var receiveStopwatch = Stopwatch.StartNew();
logger.LogInformation(
    "Async dependency {DependencyType} started at stage {GenerationStage}.",
    "Queue",
    "ReceiveBookPlan");
var received = await jobsQueue.ReceiveMessageAsync(TimeSpan.FromHours(6));
if (received.Value is null)
{
    logger.LogInformation(
        "Async dependency {DependencyType} completed with outcome {Outcome} in {ElapsedMs} ms.",
        "Queue",
        "NoMessage",
        receiveStopwatch.ElapsedMilliseconds);
    await host.StopAsync();
    return 0;
}

var queueMessage = received.Value;
logger.LogInformation(
    "Async dependency {DependencyType} completed with outcome {Outcome} in {ElapsedMs} ms.",
    "Queue",
    "MessageReceived",
    receiveStopwatch.ElapsedMilliseconds);
BookPlanGenerationRequestMessage? request = null;
try
{
    request = queueMessage.Body.ToObjectFromJson<BookPlanGenerationRequestMessage>()
        ?? throw new InvalidDataException("The queue message body is empty.");
    using var activity = OperationTelemetry.StartConsumerActivity("Process book plan message", request.TraceParent);
    using var operationScope = logger.BeginScope(
        "OperationId:{OperationId} ProjectId:{ProjectId} QueueMessageId:{QueueMessageId} Attempt:{Attempt}",
        request.RequestId,
        request.ProjectId,
        queueMessage.MessageId,
        queueMessage.DequeueCount);
    var operationStopwatch = Stopwatch.StartNew();
    logger.LogInformation(
        "Async operation {OperationName} started at stage {GenerationStage}.",
        "ProcessBookPlanMessage",
        "GenerateBookPlan");
    using var scope = host.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<BookPlanGenerationService>()
        .ProcessAsync(request, CancellationToken.None);
    await jobsQueue.DeleteMessageAsync(queueMessage.MessageId, queueMessage.PopReceipt);
    logger.LogInformation(
        "Async operation {OperationName} completed with outcome {Outcome} in {ElapsedMs} ms.",
        "ProcessBookPlanMessage",
        "Succeeded",
        operationStopwatch.ElapsedMilliseconds);
    await host.StopAsync();
    return 0;
}
catch (OperationCanceledException exception)
{
    logger.LogWarning(
        exception,
        "Async operation {OperationName} ended with outcome {Outcome} at stage {GenerationStage} for operation {OperationId}, project {ProjectId}, queue message {QueueMessageId}, and attempt {Attempt}.",
        "ProcessBookPlanMessage",
        "TimedOut",
        "GenerateBookPlan",
        request?.RequestId,
        request?.ProjectId,
        queueMessage.MessageId,
        queueMessage.DequeueCount);
    await host.StopAsync();
    return 1;
}
catch (Exception exception)
{
    if (queueMessage.DequeueCount >= maximumDequeueCount)
    {
        logger.LogError(
            exception,
            "Async operation {OperationName} failed with outcome {Outcome} at stage {GenerationStage} for operation {OperationId}, project {ProjectId}, queue message {QueueMessageId}, and attempt {Attempt}.",
            "ProcessBookPlanMessage",
            "TerminalFailure",
            "GenerateBookPlan",
            request?.RequestId,
            request?.ProjectId,
            queueMessage.MessageId,
            queueMessage.DequeueCount);
        await jobsQueue.DeleteMessageAsync(queueMessage.MessageId, queueMessage.PopReceipt);
        logger.LogError(
            "Async dependency {DependencyType} completed with outcome {Outcome} for queue message {QueueMessageId} after {Attempt} attempts.",
            "Queue",
            "DeletedAfterTerminalFailure",
            queueMessage.MessageId,
            queueMessage.DequeueCount);
    }
    else
    {
        logger.LogWarning(
            exception,
            "Async operation {OperationName} failed with outcome {Outcome} at stage {GenerationStage} for operation {OperationId}, project {ProjectId}, queue message {QueueMessageId}, and attempt {Attempt}; queue redelivery will retry it.",
            "ProcessBookPlanMessage",
            "RetryScheduled",
            "GenerateBookPlan",
            request?.RequestId,
            request?.ProjectId,
            queueMessage.MessageId,
            queueMessage.DequeueCount);
    }

    await host.StopAsync();
    return 1;
}
