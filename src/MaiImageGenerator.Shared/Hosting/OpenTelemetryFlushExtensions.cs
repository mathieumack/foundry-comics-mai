// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// 
// This solution is provided for testing, learning, and evaluation purposes only.
// It is not intended for production use. Microsoft provides no support or guarantees.
// Use at your own risk.

using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MaiImageGenerator.Jobs.BoardGeneration.Hosting;

/// <summary>
/// Extension methods for flushing OpenTelemetry providers before process exit.
/// </summary>
public static class OpenTelemetryFlushExtensions
{
    /// <summary>
    /// Forces a flush of all registered OpenTelemetry providers (traces, metrics, logs).
    /// <para>
    /// This is critical for short-lived processes such as Container App Jobs: the default
    /// batch export interval is typically 5 seconds, so any telemetry emitted near process
    /// exit would be silently dropped without an explicit flush.
    /// </para>
    /// </summary>
    /// <param name="serviceProvider">The application service provider.</param>
    /// <param name="timeoutMilliseconds">
    /// Maximum time in milliseconds to wait for each provider to flush. Defaults to 5000.
    /// </param>
    public static void FlushOpenTelemetry(this IServiceProvider serviceProvider, int timeoutMilliseconds = 5000)
    {
        serviceProvider.GetService<TracerProvider>()?.ForceFlush(timeoutMilliseconds);
        serviceProvider.GetService<MeterProvider>()?.ForceFlush(timeoutMilliseconds);
        serviceProvider.GetService<LoggerProvider>()?.ForceFlush(timeoutMilliseconds);
    }
}
