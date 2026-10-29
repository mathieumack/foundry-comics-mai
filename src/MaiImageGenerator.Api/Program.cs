using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Api.Hubs;
using MaiImageGenerator.Api.Services;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Services;
using MaiImageGenerator.Shared.Generation;
using MDev.Dotnet.AspNetCore.Apis.Extensions;
using MDev.Dotnet.AspNetCore.OpenTelemetry.Apis.Extensions;
using MDev.Dotnet.Azure.CosmosDb.Startup;
using MDev.Dotnet.Azure.ContainerApps.Authentication.Extensions;
using MDev.Dotnet.Azure.StorageAccount.Startup;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.SignalR;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using ServiceCollectionHelpers.AssemblyFinder;
using System.ClientModel.Primitives;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);
var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ManagedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"]
});

// Register configuration
var customMeters = new List<string>
{
    ImageGenerationCoordinator.MeterName,
    AiTraceCapture.MeterName
};

builder.RegisterConfiguration()
        .RegisterOpenTelemetry(customMeters)
        .RegisterControllers<Program>()
        .RegisterOpenApi(forceHttpsServers: true, includeServerUrls: false)
        .RegisterAzureStorage(credential)
        .RegisterCosmosDb<MaiImageGeneratorDbContext>(credential)
        .BindConfiguration<FoundryOptions>(FoundryOptions.SectionName);

var serviceBusNamespace = builder.Configuration["ServiceBus:FullyQualifiedNamespace"]
    ?? throw new InvalidOperationException("Configuration 'ServiceBus:FullyQualifiedNamespace' is required.");
var jobsQueueName = builder.Configuration["ServiceBus:JobsQueueName"]
    ?? throw new InvalidOperationException("Configuration 'ServiceBus:JobsQueueName' is required.");
var bookPlanJobsQueueName = builder.Configuration["ServiceBus:BookPlanJobsQueueName"]
    ?? throw new InvalidOperationException("Configuration 'ServiceBus:BookPlanJobsQueueName' is required.");

builder.Services.AddSingleton<TokenCredential>(credential);
builder.Services.AddSingleton(new ServiceBusClient(serviceBusNamespace, credential));
builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<ServiceBusClient>().CreateSender(jobsQueueName));
builder.Services.AddSingleton(serviceProvider => new BookPlanGenerationQueue(
    serviceProvider.GetRequiredService<ServiceBusClient>().CreateSender(bookPlanJobsQueueName)));
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
            instructions: MafPromptRewriter.Instructions,
            name: "PromptRewriter");
});
builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<FoundryOptions>>().Value;
    return serviceProvider.GetRequiredService<OpenAIClient>().GetImageClient(options.ImageModel);
});
builder.Services.AddScoped<IPromptRewriter, MafPromptRewriter>();
builder.Services.AddScoped<IImageGenerator, FoundryImageGenerator>();
builder.Services.AddSingleton(serviceProvider =>
    new ImageGenerationCoordinator(serviceProvider.GetRequiredService<IOptions<FoundryOptions>>().Value));
builder.Services.AddHttpClient();
builder.Services.AddHttpClient(
    FoundryImageGenerator.HttpClientName,
    client => client.Timeout = TimeSpan.FromMinutes(9));
builder.Services.AddDaprClient();
builder.Services.AddScoped<ReferenceService>();
builder.Services.AddScoped<ImageComposer>();
builder.Services.AddScoped<GenerationService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<ScaffoldTraceStore>();
builder.Services.AddScoped<IProjectDeletionRepository, CosmosProjectDeletionRepository>();
builder.Services.AddScoped<IProjectArtifactStore, BlobProjectArtifactStore>();
builder.Services.AddScoped<ProjectDeletionService>();
builder.Services.AddScoped<ProjectScaffolder>();
builder.Services.AddScoped<BookService>();
builder.Services.AddSingleton<AuthenticatedUserIdProvider>();
builder.Services.AddSingleton<IUserIdProvider>(
    serviceProvider => serviceProvider.GetRequiredService<AuthenticatedUserIdProvider>());
builder.Services.AddSignalR()
    .AddAzureSignalR(options =>
    {
        var connectionString = builder.Configuration["SignalR:ConnectionString"]
            ?? throw new InvalidOperationException("Configuration 'SignalR:ConnectionString' is required.");
        var endpoint = new ServiceEndpoint(connectionString);
        options.Endpoints = [new ServiceEndpoint(new Uri(endpoint.Endpoint), credential)];
        options.ClaimsProvider = context =>
        [
            new Claim(
                ClaimTypes.NameIdentifier,
                context.RequestServices
                    .GetRequiredService<AuthenticatedUserIdProvider>()
                    .GetRequiredUserId())
        ];
    });

// Register classes on IoC
builder.Services.AddHttpContextAccessor()
                .RegisterTypes(builder.Configuration)
                .AddCors(options =>
                {
                    options.AddPolicy("AllowFrontend", policy =>
                    {
                        policy.AllowAnyOrigin()   // Permet n'importe quel domaine d'origine
                                .AllowAnyMethod()   // Permet toutes les méthodes HTTP (GET, POST, PUT, DELETE, etc.)
                                .AllowAnyHeader();  // Permet tous les headers
                    });
                });

var app = builder.Build();

app.UseCors("AllowFrontend");

// Only use HTTPS redirection in development
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.MapControllers();

// Map SignalR hub
app.MapHub<NotificationHub>("/hubs/notifications");

app.Run();