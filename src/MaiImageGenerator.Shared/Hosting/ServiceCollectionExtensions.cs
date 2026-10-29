// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.

using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using MaiImageGenerator.Jobs.BoardGeneration.Configuration;
using MaiImageGenerator.Jobs.BoardGeneration.Hosting;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Generation;
using MDev.Dotnet.Azure.CosmosDb.Startup;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ServiceCollectionHelpers.AssemblyFinder.Attributes;
using System.Reflection;
using System.Text.RegularExpressions;

namespace MaiImageGenerator.Jobs.BoardGeneration.Hosting;

/// <summary>
/// Extension methods for registering shared GraphRag services in the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Load managed identity for current services
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <param name="credential"></param>
    /// <returns></returns>
    public static Azure.Core.TokenCredential GetManagedIdentity(this IServiceCollection services,
                                                        IConfiguration configuration)
    {
        var clientId = configuration["AZURE_CLIENT_ID"];
        return new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
    }

    /// <summary>
    /// Register logging configuration for job or hosted service
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <param name="serviceName"></param>
    /// <returns></returns>
    public static IServiceCollection AddLogging(this IServiceCollection services, 
                                                IConfiguration configuration,
                                                string serviceName)
    {
        // Configure Logging with Console – Warning+ by default; application namespaces at Information.
        // The filter uses serviceName so it applies correctly to both DocumentProcessingJob and DocumentSyncJob.
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.SetMinimumLevel(LogLevel.Warning);
            logging.AddFilter(serviceName, LogLevel.Information);
        });

        // TracerProvider and MeterProvider are always registered in DI so ForceFlush() can be called
        // before the process exits. Azure Monitor exporters are only wired up when the connection
        // string is present – registering them with a null/empty string causes silent export failures.
        var appInsightsConnectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        var resourceBuilder = ResourceBuilder.CreateDefault().AddService(serviceName: serviceName, serviceVersion: "1.0.0");

        var otelBuilder = services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(serviceName)
                    .SetResourceBuilder(resourceBuilder);

                if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
                    tracing.AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsightsConnectionString);
            })
            .WithMetrics(metrics =>
            {
                metrics.SetResourceBuilder(resourceBuilder)
                       .AddMeter(AiTraceCapture.MeterName)
                       .AddRuntimeInstrumentation();

                if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
                    metrics.AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsightsConnectionString);
            });

        // OTel log bridge to Azure Monitor: only wire up when the connection string is present.
        // Wiring it with an empty connection string intercepts ILogger calls but drops them silently.
        if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
        {
            otelBuilder.WithLogging(logging =>
                logging.SetResourceBuilder(resourceBuilder)
                       .AddAzureMonitorLogExporter(o => o.ConnectionString = appInsightsConnectionString));
        }

        return services;
    }

    /// <summary>
    /// Registers the shared Cosmos DB context (AppDbContext) using the configured options.
    /// </summary>
    public static IServiceCollection AddCosmosDb(
        this IServiceCollection services,
        IConfiguration configuration,
        Azure.Core.TokenCredential credential)
    {
        services.RegisterCosmosDb<MaiImageGeneratorDbContext>(configuration, credential);
        return services;
    }

    /// <summary>
    /// Binds all shared GraphRag configuration sections and registers them as IOptions&lt;T&gt;.
    /// </summary>
    public static IServiceCollection AddSharedConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AISearchOptions>(configuration.GetSection(AISearchOptions.SectionName));
        services.Configure<AzureFoundryOptions>(configuration.GetSection(AzureFoundryOptions.SectionName));

        return services;
    }

    /// <summary>
    /// Registers all shared service and repository implementations discovered via the
    /// <see cref="ServiceRegisterAttribute"/> in the shared library assembly.
    /// <para>
    /// Concrete types decorated with <see cref="ServiceRegisterAttribute"/> are automatically
    /// registered without requiring manual additions to this method. The concrete
    /// <c>IFileRepository</c> implementation is selected based on the <c>FILE_REPOSITORY</c>
    /// configuration value via the attribute's <see cref="ServiceRegisterAttribute.ConfigurationKey"/>
    /// and <see cref="ServiceRegisterAttribute.ConfigurationKeyFormat"/> properties:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>FileShare</c> (case-insensitive) → <c>FileShareRepository</c></item>
    ///   <item><c>BlobStorage</c> (case-insensitive) → <c>BlobStorageRepository</c></item>
    /// </list>
    /// When <c>FILE_REPOSITORY</c> is not configured, <c>IFileRepository</c> is not registered.
    /// </summary>
    public static IServiceCollection AddGraphRagSharedServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var assembly = Assembly.GetAssembly(typeof(ServiceCollectionExtensions))!;

        foreach (var type in assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract))
        {
            var attribute = type.GetCustomAttribute<ServiceRegisterAttribute>();
            if (attribute is null)
                continue;

            // Skip conditionally registered types whose configuration key is absent or whose
            // value does not match the expected format.
            if (!string.IsNullOrWhiteSpace(attribute.ConfigurationKey))
            {
                var configValue = configuration[attribute.ConfigurationKey] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(configValue))
                    continue;

                if (!string.IsNullOrWhiteSpace(attribute.ConfigurationKeyFormat)
                    && !Regex.IsMatch(configValue, attribute.ConfigurationKeyFormat, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)))
                {
                    continue;
                }
            }

            if (attribute.RegisterWithInterface)
            {
                foreach (var interfaceType in type.GetInterfaces()
                    .Where(i => !i.IsGenericType
                             && i != typeof(IDisposable)
                             && i != typeof(IAsyncDisposable)
                             && i.Namespace?.StartsWith("GraphRag.", StringComparison.Ordinal) == true))
                {
                    RegisterService(services, interfaceType, type, attribute.Scope);
                }
            }
            else
            {
                RegisterService(services, type, type, attribute.Scope);
            }
        }

        return services;
    }

    private static void RegisterService(IServiceCollection services, Type serviceType, Type implementationType, ServiceLifetime lifetime)
    {
        switch (lifetime)
        {
            case ServiceLifetime.Singleton:
                services.AddSingleton(serviceType, implementationType);
                break;
            case ServiceLifetime.Scoped:
                services.AddScoped(serviceType, implementationType);
                break;
            case ServiceLifetime.Transient:
                services.AddTransient(serviceType, implementationType);
                break;
        }
    }
}
