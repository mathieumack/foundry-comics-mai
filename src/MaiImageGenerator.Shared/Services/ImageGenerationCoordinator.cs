using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using MaiImageGenerator.Api.Configuration;

namespace MaiImageGenerator.Shared.Services;

public sealed class ImageGenerationCoordinator
{
    public const string MeterName = "MaiImageGenerator.ImageGeneration";

    private const int MaximumBackoffMilliseconds = 30_000;
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> QueueDuration = Meter.CreateHistogram<double>(
        "image_generation.queue.duration",
        "ms");
    private static readonly Histogram<double> GenerationDuration = Meter.CreateHistogram<double>(
        "image_generation.duration",
        "ms");
    private static readonly Histogram<double> BatchDuration = Meter.CreateHistogram<double>(
        "image_generation.batch.duration",
        "ms");
    private static readonly Histogram<int> BatchSize = Meter.CreateHistogram<int>(
        "image_generation.batch.size",
        "{image}");
    private static readonly UpDownCounter<int> ActiveRequests = Meter.CreateUpDownCounter<int>(
        "image_generation.active",
        "{request}");
    private static readonly Counter<long> RetryCount = Meter.CreateCounter<long>(
        "image_generation.retries",
        "{retry}");
    private static readonly Counter<long> ThrottleCount = Meter.CreateCounter<long>(
        "image_generation.throttles",
        "{response}");

    private readonly int _maxParallelImages;
    private readonly int _retryCount;
    private readonly int _retryBaseDelayMilliseconds;

    public ImageGenerationCoordinator(FoundryOptions options)
    {
        _maxParallelImages = options.MaxParallelImages is >= 1 and <= 16
            ? options.MaxParallelImages
            : throw new InvalidOperationException("Foundry:MaxParallelImages must be between 1 and 16.");
        _retryCount = options.ImageRetryCount is >= 0 and <= 10
            ? options.ImageRetryCount
            : throw new InvalidOperationException("Foundry:ImageRetryCount must be between 0 and 10.");
        _retryBaseDelayMilliseconds = options.ImageRetryBaseDelayMilliseconds is >= 1 and <= MaximumBackoffMilliseconds
            ? options.ImageRetryBaseDelayMilliseconds
            : throw new InvalidOperationException(
                $"Foundry:ImageRetryBaseDelayMilliseconds must be between 1 and {MaximumBackoffMilliseconds}.");
    }

    public async Task RunAsync<T>(
        IReadOnlyCollection<T> items,
        string workflow,
        Func<T, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        var batchStarted = Stopwatch.GetTimestamp();
        var failures = new ConcurrentQueue<Exception>();
        BatchSize.Record(items.Count, new KeyValuePair<string, object?>("workflow", workflow));
        try
        {
            await Parallel.ForEachAsync(
                items,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = _maxParallelImages,
                    CancellationToken = cancellationToken
                },
                async (item, token) =>
                {
                    QueueDuration.Record(
                        Stopwatch.GetElapsedTime(batchStarted).TotalMilliseconds,
                        new KeyValuePair<string, object?>("workflow", workflow));
                    try
                    {
                        await operation(item, token);
                    }
                    catch (Exception exception) when (
                        exception is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        failures.Enqueue(exception);
                    }
                });
            if (!failures.IsEmpty)
            {
                throw new AggregateException(failures);
            }
        }
        finally
        {
            BatchDuration.Record(
                Stopwatch.GetElapsedTime(batchStarted).TotalMilliseconds,
                new KeyValuePair<string, object?>("workflow", workflow),
                new KeyValuePair<string, object?>("max_concurrency", _maxParallelImages));
        }
    }

    public async Task<T> ExecuteWithRetryAsync<T>(
        string workflow,
        Func<CancellationToken, Task<T>> operation,
        Func<Exception, bool> isTransient,
        Func<Exception, bool> isThrottled,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var generationStarted = Stopwatch.GetTimestamp();
            ActiveRequests.Add(1, new KeyValuePair<string, object?>("workflow", workflow));
            try
            {
                return await operation(cancellationToken);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                if (isThrottled(exception))
                {
                    ThrottleCount.Add(1, new KeyValuePair<string, object?>("workflow", workflow));
                }

                if (attempt >= _retryCount || !isTransient(exception))
                {
                    throw;
                }

                RetryCount.Add(
                    1,
                    new KeyValuePair<string, object?>("workflow", workflow),
                    new KeyValuePair<string, object?>("attempt", attempt + 1));
                var delay = Math.Min(
                    _retryBaseDelayMilliseconds * Math.Pow(2, attempt),
                    MaximumBackoffMilliseconds);
                await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken);
            }
            finally
            {
                ActiveRequests.Add(-1, new KeyValuePair<string, object?>("workflow", workflow));
                GenerationDuration.Record(
                    Stopwatch.GetElapsedTime(generationStarted).TotalMilliseconds,
                    new KeyValuePair<string, object?>("workflow", workflow),
                    new KeyValuePair<string, object?>("attempt", attempt + 1));
            }
        }
    }
}
