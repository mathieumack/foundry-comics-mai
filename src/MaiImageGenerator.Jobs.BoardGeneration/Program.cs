using Azure.Core;
using Azure.Storage.Queues;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Jobs.BoardGeneration.Hosting;
using MaiImageGenerator.Jobs.BoardGeneration.Services;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Messages;
using MaiImageGenerator.Shared.Telemetry;
using MDev.Dotnet.AspNetCore.Apis.Extensions;
using MDev.Dotnet.Azure.CosmosDb.Startup;
using MDev.Dotnet.Azure.StorageAccount.Settings;
using MDev.Dotnet.Azure.StorageAccount.Startup;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Images;
using System.Diagnostics;
using System.ClientModel.Primitives;

const int maximumDequeueCount = 5;

var builder = Host.CreateApplicationBuilder(args);

// Managed Identity credential (uses AZURE_CLIENT_ID env var for user-assigned identity)
var credential = builder.Services.GetManagedIdentity(builder.Configuration);

// Register configuration
var customMeters = new List<string>
{
};

builder.RegisterConfiguration()
        .RegisterCosmosDb<MaiImageGeneratorDbContext>(credential)
        .RegisterAzureStorage(credential)
        .BindConfiguration<FoundryOptions>(FoundryOptions.SectionName)
        .BindConfiguration(out StorageAccountSettings storageSettings, StorageAccountSettings.SectionName);

builder.Services.AddLogging(builder.Configuration, "BoardGenerationJob");

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

var notificationQueueOptions = new QueueClientOptions
{
    MessageEncoding = storageSettings.QueueMessagesEncodeBase64
        ? QueueMessageEncoding.Base64
        : QueueMessageEncoding.None
};
builder.Services.AddSingleton(serviceProvider => new PanelGenerationNotifier(
    new QueueClient(
        new Uri($"{storageSettings.QueuesEndpoint.TrimEnd('/')}/{PanelGenerationNotifications.Started}"),
        credential,
        notificationQueueOptions),
    new QueueClient(
        new Uri($"{storageSettings.QueuesEndpoint.TrimEnd('/')}/{PanelGenerationNotifications.Completed}"),
        credential,
        notificationQueueOptions),
    serviceProvider.GetRequiredService<ILogger<PanelGenerationNotifier>>()));
builder.Services.AddScoped<ReferenceService>();
builder.Services.AddScoped<ImageComposer>();
builder.Services.AddScoped<GenerationService>();

using var host = builder.Build();

await host.StartAsync();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BoardGenerationJob");

var queueClientOptions = new QueueClientOptions();
if (storageSettings.QueueMessagesEncodeBase64)
    queueClientOptions.MessageEncoding = QueueMessageEncoding.Base64;
    
var jobsQueueName = builder.Configuration["Storage:JobsQueueName"] ?? "generation-jobs";
var jobsQueue = new QueueClient(
    new Uri($"{storageSettings.QueuesEndpoint.TrimEnd('/')}/{jobsQueueName}"),
    credential,
    queueClientOptions);

var receiveStopwatch = Stopwatch.StartNew();
logger.LogInformation(
    "Async dependency {DependencyType} started at stage {GenerationStage}.",
    "Queue",
    "ReceiveBoard");
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
BoardGenerationRequestMessage? request = null;
try
{
    request = queueMessage.Body.ToObjectFromJson<BoardGenerationRequestMessage>()
        ?? throw new InvalidDataException("The queue message body is empty.");
    using var activity = OperationTelemetry.StartConsumerActivity("Process board generation message", request.TraceParent);
    using var operationScope = logger.BeginScope(
        "OperationId:{OperationId} GenerationId:{GenerationId} ProjectId:{ProjectId} QueueMessageId:{QueueMessageId} Attempt:{Attempt}",
        request.BoardId,
        request.BoardId,
        request.ProjectId,
        queueMessage.MessageId,
        queueMessage.DequeueCount);
    var operationStopwatch = Stopwatch.StartNew();
    logger.LogInformation(
        "Async operation {OperationName} started at stage {GenerationStage}.",
        "ProcessBoardMessage",
        "GenerateBoard");
    using var scope = host.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<GenerationService>()
        .ProcessAsync(request, CancellationToken.None);
    await jobsQueue.DeleteMessageAsync(queueMessage.MessageId, queueMessage.PopReceipt);
    logger.LogInformation(
        "Async operation {OperationName} completed with outcome {Outcome} in {ElapsedMs} ms.",
        "ProcessBoardMessage",
        "Succeeded",
        operationStopwatch.ElapsedMilliseconds);
    await host.StopAsync();
    return 0;
}
catch (OperationCanceledException exception)
{
    logger.LogWarning(
        exception,
        "Async operation {OperationName} ended with outcome {Outcome} at stage {GenerationStage} for operation {OperationId}, generation {GenerationId}, project {ProjectId}, queue message {QueueMessageId}, and attempt {Attempt}.",
        "ProcessBoardMessage",
        "TimedOut",
        "GenerateBoard",
        request?.BoardId,
        request?.BoardId,
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
            "Async operation {OperationName} failed with outcome {Outcome} at stage {GenerationStage} for operation {OperationId}, generation {GenerationId}, project {ProjectId}, queue message {QueueMessageId}, and attempt {Attempt}.",
            "ProcessBoardMessage",
            "TerminalFailure",
            "GenerateBoard",
            request?.BoardId,
            request?.BoardId,
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
            "Async operation {OperationName} failed with outcome {Outcome} at stage {GenerationStage} for operation {OperationId}, generation {GenerationId}, project {ProjectId}, queue message {QueueMessageId}, and attempt {Attempt}; queue redelivery will retry it.",
            "ProcessBoardMessage",
            "RetryScheduled",
            "GenerateBoard",
            request?.BoardId,
            request?.BoardId,
            request?.ProjectId,
            queueMessage.MessageId,
            queueMessage.DequeueCount);
    }

    await host.StopAsync();
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