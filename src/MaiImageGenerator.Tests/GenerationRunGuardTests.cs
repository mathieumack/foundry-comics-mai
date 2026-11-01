using System.Text.Json;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MaiImageGenerator.Tests;

public sealed class GenerationRunGuardTests
{
    [Theory]
    [InlineData(1, 8)]
    [InlineData(3, 20)]
    [InlineData(7, 32)]
    public async Task InvokeAsync_ConcurrentReservationsStopAtExactCallCeiling(int ceiling, int contenders)
    {
        var clock = new ManualTimeProvider();
        var document = CreateDocument(clock, maxCalls: ceiling);
        var store = new JsonCheckpoint(document);
        using var guard = new GenerationRunGuard(document, store.SaveAsync, clock);
        var checkpointEntered = NewSignal();
        var releaseCheckpoint = NewSignal();
        var releaseProviders = NewSignal();
        var allDecided = NewSignal();
        var decisions = 0;
        var providerCalls = 0;
        store.BeforeSaveAsync = async (number, token) =>
        {
            if (number == 1)
            {
                checkpointEntered.SetResult();
                await releaseCheckpoint.Task.WaitAsync(token);
            }
        };
        void Decided()
        {
            if (Interlocked.Increment(ref decisions) == contenders)
                allDecided.TrySetResult();
        }

        var calls = Enumerable.Range(0, contenders).Select(async number =>
        {
            try
            {
                await guard.InvokeAsync(
                    $"panel-{number}", "panel-image", "attempt-1", 0, document.ScaffoldTraces,
                    async (trace, token) =>
                    {
                        Interlocked.Increment(ref providerCalls);
                        Decided();
                        await releaseProviders.Task.WaitAsync(token);
                        trace.Status = "completed";
                        return number;
                    }, CancellationToken.None);
                return (string?)null;
            }
            catch (GenerationBudgetExceededException exception)
            {
                Decided();
                return exception.Reason;
            }
        }).ToArray();

        try
        {
            await checkpointEntered.Task.WaitAsync(TestTimeout);
            Assert.Equal(1, guard.Budget.ReservedModelCalls);
            Assert.Equal(0, providerCalls);
            Assert.Null(store.Json);
            releaseCheckpoint.SetResult();
            await allDecided.Task.WaitAsync(TestTimeout);

            Assert.Equal(ceiling, providerCalls);
            Assert.Equal(ceiling, guard.Budget.ReservedModelCalls);
            Assert.Equal(ceiling, document.ScaffoldTraces.Count);
            Assert.Equal(ceiling, guard.Budget.Stages.Count);
            Assert.All(guard.Budget.Stages, stage => Assert.Equal(1, stage.ReservedCalls));
            Assert.All(document.ScaffoldTraces, trace => Assert.Equal(0, trace.RetryNumber));
            Assert.Equal("call-limit", guard.Budget.StopReason);
            Assert.Equal(ceiling, store.Reload().Budget!.ReservedModelCalls);
            Assert.Equal("call-limit", store.Reload().Budget!.StopReason);
        }
        finally
        {
            releaseCheckpoint.TrySetResult();
            releaseProviders.TrySetResult();
            await Task.WhenAll(calls).WaitAsync(TestTimeout);
        }

        var outcomes = await Task.WhenAll(calls);
        Assert.Equal(ceiling, outcomes.Count(reason => reason is null));
        Assert.Equal(contenders - ceiling, outcomes.Count(reason => reason == "call-limit"));
        Assert.Equal(ceiling, store.Reload().Budget!.ReservedModelCalls);
    }

    [Fact]
    public async Task InvokeAsync_JsonRedeliveryRetainsCallsRetriesAttemptsAndOriginalDeadline()
    {
        var clock = new ManualTimeProvider();
        var document = CreateDocument(clock, maxAttempts: 2);
        var deadline = document.Budget!.Deadline;
        var store = new JsonCheckpoint(document);
        using (var guard = new GenerationRunGuard(document, store.SaveAsync, clock))
        {
            await guard.BeginAttemptAsync(CancellationToken.None);
            for (var retry = 0; retry < 2; retry++)
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => guard.InvokeAsync<int>(
                    "panel-1", "panel-image", "attempt-1", 2, document.ScaffoldTraces,
                    (_, _) => Task.FromException<int>(new HttpRequestException("Response lost.")),
                    CancellationToken.None));
            }
        }

        clock.Advance(TimeSpan.FromSeconds(10));
        var redelivered = store.Reload();
        Assert.NotSame(document, redelivered);
        Assert.NotSame(document.Budget, redelivered.Budget);
        Assert.Equal(2, redelivered.Budget!.ReservedModelCalls);
        Assert.Equal(1, redelivered.Budget.ReservedAttempts);
        Assert.Equal(2, Assert.Single(redelivered.Budget.Stages).ReservedCalls);
        Assert.Equal(deadline, redelivered.Budget.Deadline);
        Assert.Equal(new int?[] { 0, 1 }, redelivered.ScaffoldTraces.Select(trace => trace.RetryNumber));

        var redeliveryStore = new JsonCheckpoint(redelivered);
        using var resumed = new GenerationRunGuard(redelivered, redeliveryStore.SaveAsync, clock);
        await resumed.BeginAttemptAsync(CancellationToken.None);
        var result = await resumed.InvokeAsync(
            "panel-1", "panel-image", "attempt-2", 2, redelivered.ScaffoldTraces,
            (trace, _) =>
            {
                var reserved = redeliveryStore.Reload();
                Assert.Equal(3, reserved.Budget!.ReservedModelCalls);
                Assert.Equal(2, reserved.Budget.ReservedAttempts);
                Assert.Equal(3, Assert.Single(reserved.Budget.Stages).ReservedCalls);
                Assert.Equal(deadline, reserved.Budget.Deadline);
                Assert.Equal("reserved", reserved.ScaffoldTraces.Last().Status);
                Assert.Equal("attempt-2", trace.AttemptId);
                Assert.Equal(2, trace.RetryNumber);
                trace.Status = "completed";
                return Task.FromResult(42);
            }, CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(deadline, redeliveryStore.Reload().Budget!.Deadline);
        Assert.Equal(new[] { "attempt-1", "attempt-1", "attempt-2" },
            redeliveryStore.Reload().ScaffoldTraces.Select(trace => trace.AttemptId));
        var exhausted = await Assert.ThrowsAsync<GenerationBudgetExceededException>(() => resumed.InvokeAsync(
            "panel-1", "panel-image", "attempt-2", 2, redelivered.ScaffoldTraces,
            MustNotInvoke, CancellationToken.None));
        Assert.Equal("retry-limit", exhausted.Reason);
        Assert.Equal(3, redeliveryStore.Reload().Budget!.ReservedModelCalls);
        Assert.Equal("retry-limit", redeliveryStore.Reload().Budget!.StopReason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task BeginAttemptAsync_ExhaustionIsDurableAcrossRedelivery(int maximum)
    {
        var clock = new ManualTimeProvider();
        var document = CreateDocument(clock, maxAttempts: maximum);
        var store = new JsonCheckpoint(document);
        using var guard = new GenerationRunGuard(document, store.SaveAsync, clock);
        for (var attempt = 0; attempt < maximum; attempt++)
            await guard.BeginAttemptAsync(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<GenerationBudgetExceededException>(
            () => guard.BeginAttemptAsync(CancellationToken.None));
        Assert.Equal("attempt-limit", exception.Reason);
        var restored = store.Reload();
        Assert.Equal(maximum, restored.Budget!.ReservedAttempts);
        Assert.Equal(0, restored.Budget.ReservedModelCalls);
        Assert.Equal("attempt-limit", restored.Budget.StopReason);

        var redeliveryStore = new JsonCheckpoint(restored);
        using var resumed = new GenerationRunGuard(restored, redeliveryStore.SaveAsync, clock);
        var repeated = await Assert.ThrowsAsync<GenerationBudgetExceededException>(
            () => resumed.BeginAttemptAsync(CancellationToken.None));
        Assert.Equal("attempt-limit", repeated.Reason);
        Assert.Equal(maximum, redeliveryStore.Reload().Budget!.ReservedAttempts);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    [InlineData(1, 3)]
    [InlineData(3, 1)]
    public async Task InvokeAsync_RetryExhaustionUsesStricterLimitAndNeverRefundsFailures(
        int requestedRetries, int budgetRetries)
    {
        var clock = new ManualTimeProvider();
        var document = CreateDocument(clock, maxRetries: budgetRetries);
        var store = new JsonCheckpoint(document);
        using var guard = new GenerationRunGuard(document, store.SaveAsync, clock);
        var allowedCalls = 1 + Math.Min(requestedRetries, budgetRetries);
        var providerCalls = 0;
        var failure = new HttpRequestException("Provider response unknown.");
        for (var call = 0; call < allowedCalls; call++)
        {
            var actual = await Assert.ThrowsAsync<HttpRequestException>(() => guard.InvokeAsync<int>(
                "panel-1", "panel-image", "attempt-1", requestedRetries, document.ScaffoldTraces,
                (_, _) =>
                {
                    providerCalls++;
                    return Task.FromException<int>(failure);
                }, CancellationToken.None));
            Assert.Same(failure, actual);
        }

        var exception = await Assert.ThrowsAsync<GenerationBudgetExceededException>(() => guard.InvokeAsync(
            "panel-1", "panel-image", "attempt-2", requestedRetries, document.ScaffoldTraces,
            MustNotInvoke, CancellationToken.None));
        Assert.Equal("retry-limit", exception.Reason);
        var saved = store.Reload();
        Assert.Equal(allowedCalls, providerCalls);
        Assert.Equal(allowedCalls, saved.Budget!.ReservedModelCalls);
        Assert.Equal(allowedCalls, Assert.Single(saved.Budget.Stages).ReservedCalls);
        Assert.Equal("retry-limit", saved.Budget.StopReason);
        Assert.Equal(Enumerable.Range(0, allowedCalls).Select(number => (int?)number),
            saved.ScaffoldTraces.Select(trace => trace.RetryNumber));

        var stickyStop = await Assert.ThrowsAsync<GenerationBudgetExceededException>(() => guard.InvokeAsync(
            "unrelated-panel", "panel-image", "attempt-2", requestedRetries, document.ScaffoldTraces,
            MustNotInvoke, CancellationToken.None));
        Assert.Equal("retry-limit", stickyStop.Reason);
        Assert.Single(store.Reload().Budget!.Stages);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task Deadline_AtOrBeyondBoundaryStopsBeforeReserving(bool beginAttempt, int secondsPast)
    {
        var clock = new ManualTimeProvider();
        var document = CreateDocument(clock);
        var store = new JsonCheckpoint(document);
        using var guard = new GenerationRunGuard(document, store.SaveAsync, clock);
        clock.Advance(TimeSpan.FromSeconds(60 + secondsPast));

        var exception = await Assert.ThrowsAsync<GenerationBudgetExceededException>(async () =>
        {
            if (beginAttempt)
                await guard.BeginAttemptAsync(CancellationToken.None);
            else
                await guard.InvokeAsync("panel-1", "panel-image", "attempt-1", 0, document.ScaffoldTraces,
                    MustNotInvoke, CancellationToken.None);
        });

        Assert.Equal("deadline", exception.Reason);
        var saved = store.Reload();
        Assert.Equal("deadline", saved.Budget!.StopReason);
        Assert.Equal(0, saved.Budget.ReservedModelCalls);
        Assert.Equal(0, saved.Budget.ReservedAttempts);
        Assert.Empty(saved.Budget.Stages);
        Assert.Empty(saved.ScaffoldTraces);
    }

    [Fact]
    public async Task InvokeAsync_DeadlineReachedDuringCheckpoint_PreventsProviderAndPersistsStop()
    {
        var clock = new ManualTimeProvider();
        var document = CreateDocument(clock);
        var store = new JsonCheckpoint(document)
        {
            BeforeSaveAsync = (number, _) =>
            {
                if (number == 1)
                    clock.Advance(TimeSpan.FromSeconds(60));
                return Task.CompletedTask;
            }
        };
        using var guard = new GenerationRunGuard(document, store.SaveAsync, clock);

        var exception = await Assert.ThrowsAsync<GenerationBudgetExceededException>(() => guard.InvokeAsync(
            "panel-1", "panel-image", "attempt-1", 0, document.ScaffoldTraces,
            MustNotInvoke, CancellationToken.None));

        Assert.Equal("deadline", exception.Reason);
        Assert.Equal(2, store.SaveCount);
        var saved = store.Reload();
        Assert.Equal(1, saved.Budget!.ReservedModelCalls);
        Assert.Equal(1, Assert.Single(saved.Budget.Stages).ReservedCalls);
        Assert.Equal("unknown", Assert.Single(saved.Budget.Stages).Status);
        AssertUnknownUsage(Assert.Single(saved.ScaffoldTraces));
        Assert.Equal("deadline", saved.Budget.StopReason);
        Assert.Equal("deadline", Assert.Single(saved.ScaffoldTraces).StopReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_UnconfirmedCheckpointPreventsProviderWithoutRefund(bool writeBeforeFailure)
    {
        var clock = new ManualTimeProvider();
        var document = CreateDocument(clock, maxCalls: 1);
        var store = new JsonCheckpoint(document);
        var failure = new IOException("Checkpoint confirmation lost.");
        Func<int, CancellationToken, Task> fail = (_, _) => Task.FromException(failure);
        if (writeBeforeFailure)
            store.AfterSaveAsync = fail;
        else
            store.BeforeSaveAsync = fail;
        using var guard = new GenerationRunGuard(document, store.SaveAsync, clock);

        var exception = await Assert.ThrowsAsync<GenerationBudgetCheckpointException>(() => guard.InvokeAsync(
            "panel-1", "panel-image", "attempt-1", 0, document.ScaffoldTraces,
            MustNotInvoke, CancellationToken.None));
        Assert.Same(failure, exception.InnerException);
        Assert.Equal(1, guard.Budget.ReservedModelCalls);
        Assert.Equal(1, Assert.Single(guard.Budget.Stages).ReservedCalls);
        AssertUnknownUsage(Assert.Single(document.ScaffoldTraces));
        Assert.Equal(writeBeforeFailure, store.Json is not null);
        if (writeBeforeFailure)
            Assert.Equal(1, store.Reload().Budget!.ReservedModelCalls);

        store.BeforeSaveAsync = null;
        store.AfterSaveAsync = null;
        var exhausted = await Assert.ThrowsAsync<GenerationBudgetExceededException>(() => guard.InvokeAsync(
            "panel-2", "panel-image", "attempt-2", 0, document.ScaffoldTraces,
            MustNotInvoke, CancellationToken.None));
        Assert.Equal("call-limit", exhausted.Reason);
        Assert.Equal(1, store.Reload().Budget!.ReservedModelCalls);
        Assert.Single(store.Reload().ScaffoldTraces);
        Assert.Equal("call-limit", store.Reload().Budget!.StopReason);
    }

    [Fact]
    public async Task BeginAttemptAsync_UnconfirmedCheckpointDoesNotRefundPersistedAttempt()
    {
        var clock = new ManualTimeProvider();
        var document = CreateDocument(clock, maxAttempts: 1);
        var failure = new IOException("Acknowledgement lost.");
        var store = new JsonCheckpoint(document)
        {
            AfterSaveAsync = (_, _) => Task.FromException(failure)
        };
        using var guard = new GenerationRunGuard(document, store.SaveAsync, clock);
        var exception = await Assert.ThrowsAsync<GenerationBudgetCheckpointException>(
            () => guard.BeginAttemptAsync(CancellationToken.None));
        Assert.Same(failure, exception.InnerException);
        Assert.Equal(1, guard.Budget.ReservedAttempts);

        var restored = store.Reload();
        var redeliveryStore = new JsonCheckpoint(restored);
        using var resumed = new GenerationRunGuard(restored, redeliveryStore.SaveAsync, clock);
        var exhausted = await Assert.ThrowsAsync<GenerationBudgetExceededException>(
            () => resumed.BeginAttemptAsync(CancellationToken.None));
        Assert.Equal("attempt-limit", exhausted.Reason);
        Assert.Equal(1, redeliveryStore.Reload().Budget!.ReservedAttempts);
    }

    [Fact]
    public async Task InvokeAsync_FinalCheckpointFailureIsNotReportedAsSuccess()
    {
        var clock = new ManualTimeProvider();
        var document = CreateDocument(clock);
        var failure = new IOException("Final checkpoint failed.");
        var store = new JsonCheckpoint(document)
        {
            BeforeSaveAsync = (number, _) => number == 2 ? Task.FromException(failure) : Task.CompletedTask
        };
        using var guard = new GenerationRunGuard(document, store.SaveAsync, clock);
        var providerCalls = 0;

        var exception = await Assert.ThrowsAsync<GenerationBudgetCheckpointException>(() => guard.InvokeAsync(
            "panel-1", "panel-image", "attempt-1", 0, document.ScaffoldTraces,
            (trace, _) =>
            {
                providerCalls++;
                trace.Status = "completed";
                return Task.FromResult(42);
            }, CancellationToken.None));

        Assert.Same(failure, exception.InnerException);
        Assert.Equal(1, providerCalls);
        Assert.Equal(1, guard.Budget.ReservedModelCalls);
        Assert.Equal("reserved", Assert.Single(store.Reload().ScaffoldTraces).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_CancellationBeforeProviderPreservesReservationsAndUsesUncancelledFinalCheckpoint(
        bool cancelDuringCheckpoint)
    {
        var clock = new ManualTimeProvider();
        var document = CreateDocument(clock);
        var store = new JsonCheckpoint(document);
        using var cancellation = new CancellationTokenSource();
        var checkpointTokens = new List<CancellationToken>();
        store.BeforeSaveAsync = (_, token) =>
        {
            checkpointTokens.Add(token);
            return Task.CompletedTask;
        };
        store.AfterSaveAsync = (number, _) =>
        {
            if (number == 1)
                cancellation.Cancel();
            return Task.CompletedTask;
        };
        if (!cancelDuringCheckpoint)
            cancellation.Cancel();
        using var guard = new GenerationRunGuard(document, store.SaveAsync, clock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guard.InvokeAsync(
            "panel-1", "panel-image", "attempt-1", 0, document.ScaffoldTraces,
            MustNotInvoke, cancellation.Token));

        Assert.Equal(cancelDuringCheckpoint ? 1 : 0, guard.Budget.ReservedModelCalls);
        if (cancelDuringCheckpoint)
        {
            Assert.Equal(new[] { cancellation.Token, CancellationToken.None }, checkpointTokens);
            Assert.Equal(1, store.Reload().Budget!.ReservedModelCalls);
            AssertUnknownUsage(Assert.Single(store.Reload().ScaffoldTraces));
        }
        else
        {
            Assert.Empty(checkpointTokens);
            Assert.Empty(document.ScaffoldTraces);
            Assert.Null(store.Json);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_MissingProviderMetadataRemainsExplicitAcrossJsonCheckpoint(bool responseLost)
    {
        var clock = new ManualTimeProvider();
        var document = CreateDocument(clock);
        var store = new JsonCheckpoint(document);
        using var guard = new GenerationRunGuard(document, store.SaveAsync, clock);
        var failure = new HttpRequestException("Response lost after possible provider work.");
        var call = guard.InvokeAsync(
            "panel-1", "panel-image", "attempt-1", 0, document.ScaffoldTraces,
            (trace, _) =>
            {
                AssertUnknownUsage(trace);
                trace.Status = responseLost ? "failed" : "completed";
                return responseLost ? Task.FromException<int>(failure) : Task.FromResult(42);
            }, CancellationToken.None);

        if (responseLost)
            Assert.Same(failure, await Assert.ThrowsAsync<HttpRequestException>(() => call));
        else
            Assert.Equal(42, await call);

        var saved = store.Reload();
        var trace = Assert.Single(saved.ScaffoldTraces);
        AssertUnknownUsage(trace);
        Assert.Null(trace.RequestId);
        Assert.Null(trace.ImageCount);
        Assert.Equal(responseLost ? "failed" : "completed", trace.Status);
        Assert.Equal("unknown", Assert.Single(saved.Budget!.Stages).Status);
        Assert.Equal(1, saved.Budget.ReservedModelCalls);
    }

    [Fact]
    public async Task InvokeAsync_ExplicitZeroUsageIsNotConfusedWithUnavailableOrInferredTotals()
    {
        var clock = new ManualTimeProvider();
        var document = CreateDocument(clock);
        var store = new JsonCheckpoint(document);
        using var guard = new GenerationRunGuard(document, store.SaveAsync, clock);

        await guard.InvokeAsync(
            "panel-1", "panel-image", "attempt-1", 0, document.ScaffoldTraces,
            (trace, _) =>
            {
                trace.ProviderOutcome = "responded";
                trace.UsageSource = "provider-reported";
                trace.InputTokens = 0;
                trace.Status = "completed";
                return Task.FromResult(42);
            }, CancellationToken.None);

        var saved = Assert.Single(store.Reload().ScaffoldTraces);
        Assert.Equal("responded", saved.ProviderOutcome);
        Assert.Equal("provider-reported", saved.UsageSource);
        Assert.Equal(0L, saved.InputTokens);
        Assert.Null(saved.OutputTokens);
        Assert.Null(saved.TotalTokens);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InvokeAsync_CorrelationAndStructuredLogsExcludeStoryAndPromptOnSuccessOrFailure(
        bool hasSnapshot, bool providerFailed)
    {
        var clock = new ManualTimeProvider();
        var document = CreateDocument(clock);
        document.Title = "private-story-title";
        document.Description = "private-story-description";
        document.Context = "private-story-context";
        document.Panels =
        [
            new GenerationPanel
            {
                Number = 1, Prompt = "private-panel-prompt", EffectivePrompt = "private-effective-prompt"
            }
        ];
        document.InputSnapshot = hasSnapshot
            ? new GenerationInputSnapshot
            {
                RunId = document.Id,
                Title = document.Title,
                Description = document.Description,
                Context = document.Context,
                Panels = [new GenerationInputPanel { Number = 1, Prompt = document.Panels[0].Prompt }],
                Characters = [new GenerationInputCharacter { Id = "character-1", Name = "private-character-name" }]
            }
            : null;
        var store = new JsonCheckpoint(document);
        var logger = new RecordingLogger();
        using var guard = new GenerationRunGuard(document, store.SaveAsync, clock, logger);
        var failure = new HttpRequestException("private-provider-response-with-prompt");
        var call = guard.InvokeAsync(
            "panel-1-image", "panel-image", "attempt-1", 0, document.Panels[0].Traces,
            (trace, _) =>
            {
                trace.Status = providerFailed ? "failed" : "completed";
                trace.ProviderOutcome = providerFailed ? "responded" : "succeeded";
                return providerFailed ? Task.FromException<int>(failure) : Task.FromResult(42);
            }, CancellationToken.None);
        if (providerFailed)
            Assert.Same(failure, await Assert.ThrowsAsync<HttpRequestException>(() => call));
        else
            Assert.Equal(42, await call);

        var saved = store.Reload();
        var trace = Assert.Single(saved.Panels[0].Traces);
        Assert.Equal(document.Id, trace.RunId);
        Assert.Equal("panel-1-image", trace.StageId);
        Assert.Equal("panel-image", trace.Operation);
        Assert.Equal("attempt-1", trace.AttemptId);
        Assert.Equal(hasSnapshot ? 1 : (int?)null, trace.InputVersion);
        Assert.Equal(0, trace.RetryNumber);
        Assert.Equal(clock.GetUtcNow(), trace.StartedAt);
        Assert.Equal(trace.StageId, Assert.Single(saved.Budget!.Stages).Id);
        Assert.Equal(trace.AttemptId, Assert.Single(saved.Budget.Stages).AttemptId);
        var correlation = JsonSerializer.Serialize(new { saved.Budget, saved.Panels[0].Traces }, JsonOptions);
        Assert.DoesNotContain("private-", correlation);
        Assert.Equal(new[] { "processing", providerFailed ? "failed" : "completed" },
            logger.Entries.Select(entry => Assert.IsType<string>(entry.Properties["Status"])));
        var allowedFields = new[]
        {
            "{OriginalFormat}", "RunId", "StageId", "Operation", "AttemptId",
            "InputVersion", "Status", "ReservedCalls", "StopReason"
        };
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Null(entry.Exception);
            Assert.Equal(allowedFields.Order(StringComparer.Ordinal),
                entry.Properties.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(document.Id, entry.Properties["RunId"]);
            Assert.Equal(trace.StageId, entry.Properties["StageId"]);
            Assert.Equal(trace.Operation, entry.Properties["Operation"]);
            Assert.Equal(trace.AttemptId, entry.Properties["AttemptId"]);
            Assert.Equal((object?)trace.InputVersion, entry.Properties["InputVersion"]);
            Assert.Equal(1, entry.Properties["ReservedCalls"]);
            Assert.Null(entry.Properties["StopReason"]);
            Assert.Contains(document.Id, entry.Message);
            Assert.Contains(trace.StageId!, entry.Message);
            Assert.Contains(trace.AttemptId!, entry.Message);
            Assert.DoesNotContain("private-", entry.Message);
            Assert.DoesNotContain("private-", JsonSerializer.Serialize(entry.Properties, JsonOptions));
        });
        foreach (var field in new[] { "title", "description", "context", "prompt", "effectivePrompt", "characters", "story" })
            Assert.DoesNotContain($"\"{field}\":", correlation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("private-panel-prompt", store.Json!);
    }

    [Theory]
    [InlineData("attempt", false)]
    [InlineData("attempt", true)]
    [InlineData("invoke", false)]
    [InlineData("invoke", true)]
    [InlineData("stage", false)]
    [InlineData("stage", true)]
    public async Task Guard_ConcurrencyFailureFromFenceOrCheckpointIsPreservedBeforeWork(
        string operation, bool failFromFence)
    {
        var clock = new ManualTimeProvider();
        var document = CreateDocument(clock);
        var conflict = new DbUpdateConcurrencyException("Stale run ownership.");
        var store = new JsonCheckpoint(document)
        {
            BeforeSaveAsync = (_, _) => Task.FromException(conflict)
        };
        using var guard = new GenerationRunGuard(document, store.SaveAsync, clock);
        var fenceCalls = 0;
        guard.Fence = () =>
        {
            fenceCalls++;
            if (failFromFence)
                throw conflict;
        };
        Task ActAsync() => operation switch
        {
            "attempt" => guard.BeginAttemptAsync(CancellationToken.None),
            "invoke" => guard.InvokeAsync("panel-1", "panel-image", "attempt-1", 0, document.ScaffoldTraces,
                MustNotInvoke, CancellationToken.None),
            "stage" => guard.StageAsync<int>("compose", "board-compose", "attempt-1",
                _ => throw new InvalidOperationException("The stage must not start."), CancellationToken.None),
            _ => throw new InvalidOperationException("Unknown test operation.")
        };

        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(ActAsync);

        Assert.Same(conflict, exception);
        Assert.Equal(1, fenceCalls);
        Assert.Equal(failFromFence ? 0 : 1, store.SaveCount);
        Assert.Null(store.Json);
        Assert.Equal(operation == "invoke" ? 1 : 0, guard.Budget.ReservedModelCalls);
        Assert.Equal(operation == "attempt" ? 1 : 0, guard.Budget.ReservedAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageAsync_CheckpointsNonProviderWorkWithoutConsumingModelCalls(bool stageFailed)
    {
        var clock = new ManualTimeProvider();
        var document = CreateDocument(clock);
        var store = new JsonCheckpoint(document);
        using var guard = new GenerationRunGuard(document, store.SaveAsync, clock);
        var failure = new IOException("Composition failed.");
        var fenceCalls = 0;
        guard.Fence = () => fenceCalls++;
        var call = guard.StageAsync("compose", "board-compose", "attempt-1",
            _ =>
            {
                var reserved = Assert.Single(store.Reload().Budget!.Stages);
                Assert.Equal("processing", reserved.Status);
                Assert.Equal("attempt-1", reserved.AttemptId);
                Assert.Equal(1, fenceCalls);
                return stageFailed ? Task.FromException<int>(failure) : Task.FromResult(42);
            }, CancellationToken.None);

        if (stageFailed)
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => call));
        else
            Assert.Equal(42, await call);

        var saved = store.Reload();
        var stage = Assert.Single(saved.Budget!.Stages);
        Assert.Equal("compose", stage.Id);
        Assert.Equal("board-compose", stage.Operation);
        Assert.Equal(stageFailed ? "failed" : "completed", stage.Status);
        Assert.Equal(0, stage.ReservedCalls);
        Assert.Equal(0, saved.Budget.ReservedModelCalls);
        Assert.Equal(0, saved.Budget.ReservedAttempts);
        Assert.Empty(saved.ScaffoldTraces);
        Assert.Equal(2, store.SaveCount);
        Assert.Equal(2, fenceCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task InvokeAsync_UnsupportedBudgetVersionFailsClosed(int version)
    {
        var clock = new ManualTimeProvider();
        var document = CreateDocument(clock);
        document.Budget!.Version = version;
        var store = new JsonCheckpoint(document);
        using var guard = new GenerationRunGuard(document, store.SaveAsync, clock);

        await Assert.ThrowsAsync<InvalidDataException>(() => guard.InvokeAsync(
            "panel-1", "panel-image", "attempt-1", 0, document.ScaffoldTraces,
            MustNotInvoke, CancellationToken.None));

        Assert.Equal(0, guard.Budget.ReservedModelCalls);
        Assert.Empty(document.ScaffoldTraces);
        Assert.Null(store.Json);
    }

    [Fact]
    public void BudgetOptions_CreateFreezesLimitsAndDeadlineWithoutSharingMutableCounters()
    {
        var clock = new ManualTimeProvider();
        var options = new GenerationBudgetOptions
        {
            MaxModelCalls = 7, MaxAttempts = 2, MaxRetriesPerStage = 1, MaxDurationSeconds = 45
        };
        var budget = options.Create(clock.GetUtcNow());
        var other = options.Create(clock.GetUtcNow());
        options.MaxModelCalls = 99;
        options.MaxDurationSeconds = 999;
        other.ReservedModelCalls++;
        other.Stages.Add(new GenerationStage { Id = "other" });

        Assert.Equal(1, budget.Version);
        Assert.Equal(7, budget.MaxModelCalls);
        Assert.Equal(2, budget.MaxAttempts);
        Assert.Equal(1, budget.MaxRetriesPerStage);
        Assert.Equal(clock.GetUtcNow().AddSeconds(45), budget.Deadline);
        Assert.Equal(0, budget.ReservedModelCalls);
        Assert.Equal(0, budget.ReservedAttempts);
        Assert.Null(budget.StopReason);
        Assert.Empty(budget.Stages);
    }

    [Theory]
    [InlineData(0, 1, 0, 1)]
    [InlineData(1001, 1, 0, 1)]
    [InlineData(1, 0, 0, 1)]
    [InlineData(1, 101, 0, 1)]
    [InlineData(1, 1, -1, 1)]
    [InlineData(1, 1, 11, 1)]
    [InlineData(1, 1, 0, 0)]
    [InlineData(1, 1, 0, 86401)]
    public void BudgetOptions_CreateRejectsUnsupportedLimits(int calls, int attempts, int retries, int seconds)
    {
        var options = new GenerationBudgetOptions
        {
            MaxModelCalls = calls, MaxAttempts = attempts, MaxRetriesPerStage = retries, MaxDurationSeconds = seconds
        };

        Assert.Throws<InvalidOperationException>(() => options.Create(new ManualTimeProvider().GetUtcNow()));
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static GenerationDocument CreateDocument(
        TimeProvider clock, int maxCalls = 8, int maxAttempts = 4, int maxRetries = 2) => new()
    {
        Id = "run-1",
        OwnerId = "owner-1",
        ProjectId = "project-1",
        InputSnapshot = new GenerationInputSnapshot { RunId = "run-1" },
        Budget = new GenerationBudgetOptions
        {
            MaxModelCalls = maxCalls,
            MaxAttempts = maxAttempts,
            MaxRetriesPerStage = maxRetries,
            MaxDurationSeconds = 60
        }.Create(clock.GetUtcNow())
    };

    private static Task<int> MustNotInvoke(AiGenerationTrace _, CancellationToken __) =>
        throw new InvalidOperationException("The provider must not be invoked.");

    private static void AssertUnknownUsage(AiGenerationTrace trace)
    {
        Assert.Equal("unknown", trace.ProviderOutcome);
        Assert.Equal("unavailable", trace.UsageSource);
        Assert.Null(trace.InputTokens);
        Assert.Null(trace.OutputTokens);
        Assert.Null(trace.TotalTokens);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }

    private sealed record LogEntry(
        LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> Properties);

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> fields
                ? fields.ToDictionary(field => field.Key, field => field.Value)
                : new Dictionary<string, object?>();
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception, properties));
        }
    }

    private sealed class JsonCheckpoint(GenerationDocument document)
    {
        public int SaveCount { get; private set; }
        public string? Json { get; private set; }
        public Func<int, CancellationToken, Task>? BeforeSaveAsync { get; set; }
        public Func<int, CancellationToken, Task>? AfterSaveAsync { get; set; }

        public async Task SaveAsync(CancellationToken token)
        {
            var number = ++SaveCount;
            if (BeforeSaveAsync is not null)
                await BeforeSaveAsync(number, token);
            Json = JsonSerializer.Serialize(document, JsonOptions);
            if (AfterSaveAsync is not null)
                await AfterSaveAsync(number, token);
        }

        public GenerationDocument Reload() =>
            Assert.IsType<GenerationDocument>(JsonSerializer.Deserialize<GenerationDocument>(Json!, JsonOptions));
    }
}
