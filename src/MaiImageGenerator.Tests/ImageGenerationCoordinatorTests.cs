using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Shared.Services;

namespace MaiImageGenerator.Tests;

public sealed class ImageGenerationCoordinatorTests
{
    [Fact]
    public async Task RunAsync_BoundsConcurrencyWhileStartingIndependentItems()
    {
        var coordinator = CreateCoordinator(maxParallelImages: 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPairStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var started = 0;

        var batch = coordinator.RunAsync(
            Enumerable.Range(0, 4).ToArray(),
            "test",
            async (_, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, current);
                if (Interlocked.Increment(ref started) == 2)
                {
                    firstPairStarted.SetResult();
                }

                await release.Task.WaitAsync(cancellationToken);
                Interlocked.Decrement(ref active);
            },
            CancellationToken.None);

        await firstPairStarted.Task;
        Assert.Equal(2, Volatile.Read(ref active));
        release.SetResult();
        await batch;
        Assert.Equal(2, maximumActive);
        Assert.Equal(4, started);
    }

    [Fact]
    public async Task RunAsync_WaitsForUnrelatedItemsBeforeReportingPartialFailure()
    {
        var coordinator = CreateCoordinator(maxParallelImages: 3);
        var completed = new List<int>();

        var exception = await Assert.ThrowsAsync<AggregateException>(() => coordinator.RunAsync(
            Enumerable.Range(0, 3).ToArray(),
            "test",
            async (item, cancellationToken) =>
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                if (item == 1)
                {
                    throw new InvalidOperationException("failed item");
                }

                lock (completed)
                {
                    completed.Add(item);
                }
            },
            CancellationToken.None));

        Assert.IsType<InvalidOperationException>(Assert.Single(exception.InnerExceptions));
        Assert.Equal([0, 2], completed.Order().ToArray());
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_RetriesTransientFailuresWithinBound()
    {
        var coordinator = CreateCoordinator(retryCount: 2);
        var attempts = 0;

        var result = await coordinator.ExecuteWithRetryAsync(
            "test",
            _ =>
            {
                if (Interlocked.Increment(ref attempts) < 3)
                {
                    throw new HttpRequestException("transient");
                }

                return Task.FromResult(42);
            },
            exception => exception is HttpRequestException,
            _ => false,
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_DoesNotRetryPermanentFailure()
    {
        var coordinator = CreateCoordinator(retryCount: 3);
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteWithRetryAsync<int>(
            "test",
            _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("permanent");
            },
            _ => false,
            _ => false,
            CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_CancellationStopsBackoff()
    {
        var coordinator = CreateCoordinator(retryCount: 10, retryDelayMilliseconds: 1000);
        using var cancellation = new CancellationTokenSource();
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = coordinator.ExecuteWithRetryAsync<int>(
            "test",
            _ =>
            {
                firstAttempt.SetResult();
                throw new HttpRequestException("transient");
            },
            exception => exception is HttpRequestException,
            _ => false,
            cancellation.Token);

        await firstAttempt.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    private static ImageGenerationCoordinator CreateCoordinator(
        int maxParallelImages = 3,
        int retryCount = 3,
        int retryDelayMilliseconds = 1) =>
        new(new FoundryOptions
        {
            MaxParallelImages = maxParallelImages,
            ImageRetryCount = retryCount,
            ImageRetryBaseDelayMilliseconds = retryDelayMilliseconds
        });

    private static void UpdateMaximum(ref int maximum, int current)
    {
        var observed = Volatile.Read(ref maximum);
        while (current > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, current, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }
}
