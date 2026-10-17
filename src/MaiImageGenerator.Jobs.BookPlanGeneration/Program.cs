using Azure.Core;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Jobs.BoardGeneration.Hosting;
using MaiImageGenerator.Jobs.BookPlanGeneration.Services;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Messages;
using MDev.Dotnet.AspNetCore.Apis.Extensions;
using MDev.Dotnet.Azure.CosmosDb.Startup;
using MDev.Dotnet.Azure.StorageAccount.Settings;
using MDev.Dotnet.Azure.StorageAccount.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
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

var received = await jobsQueue.ReceiveMessageAsync(TimeSpan.FromHours(6));
if (received.Value is null)
{
    logger.LogInformation("No book plan generation message is available.");
    await host.StopAsync();
    return 0;
}

var queueMessage = received.Value;
try
{
    var request = queueMessage.Body.ToObjectFromJson<BookPlanGenerationRequestMessage>()
        ?? throw new InvalidDataException("The queue message body is empty.");
    using var scope = host.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<BookPlanGenerationService>()
        .ProcessAsync(request, CancellationToken.None);
    await jobsQueue.DeleteMessageAsync(queueMessage.MessageId, queueMessage.PopReceipt);
    logger.LogInformation(
        "Generated book plan for project {ProjectId} and owner {OwnerId}.",
        request.ProjectId,
        request.OwnerId);
    await host.StopAsync();
    return 0;
}
catch (Exception exception)
{
    logger.LogError(
        exception,
        "Book plan generation failed for queue message {MessageId} on attempt {Attempt}.",
        queueMessage.MessageId,
        queueMessage.DequeueCount);
    if (queueMessage.DequeueCount >= maximumDequeueCount)
    {
        await jobsQueue.DeleteMessageAsync(queueMessage.MessageId, queueMessage.PopReceipt);
        logger.LogError(
            "Deleted queue message {MessageId} after {Attempt} attempts.",
            queueMessage.MessageId,
            queueMessage.DequeueCount);
    }

    await host.StopAsync();
    return 1;
}
