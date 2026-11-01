using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Shared.Data.DatabaseContext;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Generation;
using MaiImageGenerator.Shared.Messages;
using MaiImageGenerator.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Worker = MaiImageGenerator.Jobs.BoardGeneration.Services;

namespace MaiImageGenerator.Tests;

public sealed class PanelAttemptWorkerTests
{
    [Fact]
    public async Task ParallelBoardPanels_ReserveExactlyTheRunCeiling_AndKeepItOnRedelivery()
    {
        var h = new Harness();
        h.Saved.Status = GenerationStatus.Pending;
        h.Saved.Budget = new GenerationBudgetOptions { MaxModelCalls = 2 }.Create(h.Clock.GetUtcNow());
        foreach (var panel in h.Saved.Panels)
            panel.Status = PanelGenerationStatus.Pending;
        h.Message = new()
        {
            SchemaVersion = 3, BoardId = h.Saved.Id, RunId = h.Saved.Id,
            OwnerId = h.Saved.OwnerId, ProjectId = h.Saved.ProjectId
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        h.Provider.BeforeResponseAsync = async token =>
        {
            if (Interlocked.Increment(ref calls) == 2) entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        // Avoid artifact side effects in this call-ceiling test.
        h.Provider.Response = (_, _) => Json("{}", 400);
        var run = h.RunAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.Equal(2, h.Saved.Budget!.ReservedModelCalls);
        }
        finally
        {
            release.TrySetResult();
            await run;
        }
        Assert.Equal(2, calls);
        Assert.Equal(2, h.Saved.Budget!.ReservedModelCalls);
        Assert.Equal("call-limit", h.Saved.Budget.StopReason);
        Assert.Equal(GenerationStatus.Failed, h.Saved.Status);
        await h.RunAsync();
        Assert.Equal(2, calls);
        Assert.Equal(2, h.Saved.Budget.ReservedModelCalls);
    }

    [Theory]
    [InlineData("call-limit")]
    [InlineData("attempt-limit")]
    [InlineData("deadline")]
    public async Task ExhaustedRun_StopsDurablyBeforeProvider_AndRedeliveryCannotReset(string reason)
    {
        var h = new Harness();
        h.Attempt();
        var projection = h.Projection();
        h.Saved.Budget = new GenerationBudgetOptions().Create(h.Clock.GetUtcNow());
        var budget = h.Saved.Budget;
        if (reason == "call-limit") budget.ReservedModelCalls = budget.MaxModelCalls;
        if (reason == "attempt-limit") budget.ReservedAttempts = budget.MaxAttempts;
        if (reason == "deadline") budget.Deadline = h.Clock.GetUtcNow();
        await h.RunAsync();

        Assert.Equal(0, h.Provider.ImageCalls);
        Assert.Equal(0, h.Provider.RewriteCalls);
        Assert.Equal(GenerationStatus.Failed, h.Saved.Status);
        Assert.Equal(reason, h.Saved.Budget!.StopReason);
        Assert.Equal(GenerationRunGuard.DescribeStop(reason), h.Saved.Error);
        Assert.Equal(PanelGenerationStatus.Failed, h.Candidate.Status);
        Assert.Equal(projection, h.Projection());
        var saved = Serialize(h.Saved.Budget);
        await h.RunAsync();
        Assert.Equal(saved, Serialize(h.Saved.Budget));
        Assert.Equal(0, h.Provider.ImageCalls);
    }

    [Fact]
    public async Task RunDeadline_CancelsAcceptedCall_WithDurableStopAndUnknownProviderOutcome()
    {
        var h = new Harness();
        h.Attempt();
        h.Saved.Budget = new GenerationBudgetOptions { MaxDurationSeconds = 10 }.Create(h.Clock.GetUtcNow());
        h.Provider.BeforeResponseAsync = token =>
        {
            h.Clock.Advance(TimeSpan.FromSeconds(10));
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        await h.RunAsync();

        Assert.Equal(1, h.Provider.ImageCalls);
        Assert.Equal(1, h.Saved.Budget!.ReservedModelCalls);
        Assert.Equal("deadline", h.Saved.Budget.StopReason);
        Assert.Equal(GenerationStatus.Failed, h.Saved.Status);
        Assert.Null(h.Saved.PendingCommandId);
        var trace = Assert.Single(h.Candidate.Traces);
        Assert.Equal("unknown", trace.ProviderOutcome);
        Assert.Equal("deadline", trace.StopReason);
        Assert.Equal("unavailable", trace.UsageSource);
        Assert.Null(trace.TotalTokens);
        await h.RunAsync();
        Assert.Equal(1, h.Provider.ImageCalls);
    }

    [Fact]
    public async Task CorrectionsShareRunCallBudget_ButSavedVersionSelectionStillComposes()
    {
        var h = new Harness();
        h.Saved.Budget = new GenerationBudgetOptions { MaxModelCalls = 1 }.Create(h.Clock.GetUtcNow());
        h.Attempt();
        await h.RunAsync();
        Assert.Equal(1, h.Saved.Budget!.ReservedModelCalls);
        Assert.Throws<PanelCommandConflictException>(() => h.Attempt());
        h.Saved.Budget.StopReason = "call-limit";
        h.Saved.Budget.Deadline = h.Clock.GetUtcNow().AddSeconds(-1);
        h.Select();
        await h.RunAsync();

        Assert.Equal(1, h.Provider.ImageCalls);
        Assert.Equal(1, h.Saved.Budget.ReservedModelCalls);
        Assert.Equal(GenerationStatus.Completed, h.Saved.Status);
        Assert.Equal(h.CommandPath("board"), h.Saved.BoardBlobName);
    }

    [Fact]
    public async Task SixPanels_GeneratesOnlyTarget_WithoutSelectingOrRewritingLiteralPrompt()
    {
        var h = new Harness();
        h.Attempt();
        var projection = h.Projection();
        await h.RunAsync();

        Assert.Equal(1, h.Provider.ImageCalls);
        Assert.Equal(0, h.Provider.RewriteCalls);
        Assert.Equal(projection, h.Projection());
        var candidate = h.Candidate;
        Assert.Equal(PanelGenerationStatus.Completed, candidate.Status);
        Assert.Equal("Literal revised scene", candidate.EffectivePrompt);
        Assert.Equal(h.CommandPath("panel-3"), candidate.ImageBlobName);
        Assert.Equal(GenerationStatus.Completed, h.Saved.Status);
        Assert.Null(h.Saved.PendingCommandId);
        Assert.Null(h.Saved.CommandExecutionId);
        Assert.Equal(0, h.Saved.ComposedRevision);
        Assert.Single(h.Saved.Attempts);
        Assert.Equal(h.Saved.Attempts[0].Id, Assert.Single(candidate.Traces).AttemptId);
        Assert.Contains("frozen-image", Assert.Single(h.Provider.Requests));
        Assert.Contains(h.Checkpoints, item => h.CandidateIn(item).Traces.Count == 1 &&
                                              item.PendingCommandId == h.Message.CommandId);
        Assert.EndsWith($":{h.Message.CommandId}:completed", Assert.Single(h.Completed.Messages).MessageId);
        Assert.Equal("image/png", h.Blobs.LastUpload!.HttpHeaders.ContentType);
        Assert.Equal(ETag.All, h.Blobs.LastUpload.Conditions.IfNoneMatch);
    }

    [Fact]
    public async Task ExplicitSelection_ComposesSavedProjection_WithoutGeneratingImages()
    {
        var h = new Harness();
        h.Attempt();
        await h.RunAsync();
        var originalBoard = h.Saved.BoardBlobName;
        Assert.Equal(PanelVersionCommands.OriginalVersionId, h.Saved.Panels[2].SelectedVersionId);
        h.Select();
        var expected = h.Projection(includeBoard: false);
        await h.RunAsync();

        Assert.Equal(1, h.Provider.ImageCalls);
        Assert.Equal(expected, h.Projection(includeBoard: false));
        Assert.Equal(h.Message.CommandId, Assert.Single(h.Saved.SelectionCommands).Id);
        Assert.Equal(h.Saved.Revision, h.Saved.ComposedRevision);
        Assert.Equal(h.CommandPath("board"), h.Saved.BoardBlobName);
        Assert.NotEqual(originalBoard, h.Saved.BoardBlobName);
        Assert.True(h.Blobs.Content.ContainsKey(originalBoard!));
    }

    [Theory]
    [InlineData("panel")]
    [InlineData("compose")]
    public async Task CommittedDuplicate_IsNoOp(string operation)
    {
        var h = new Harness();
        h.Attempt();
        await h.RunAsync();
        if (operation == "compose")
        {
            h.Select();
            await h.RunAsync();
        }
        var json = Serialize(h.Saved);
        var uploads = h.Blobs.Uploads;
        var calls = h.Provider.ImageCalls;
        var saves = h.Checkpoints.Count;
        await h.RunAsync();
        Assert.Equal(json, Serialize(h.Saved));
        Assert.Equal(uploads, h.Blobs.Uploads);
        Assert.Equal(calls, h.Provider.ImageCalls);
        Assert.Equal(saves, h.Checkpoints.Count);
    }

    [Theory]
    [InlineData("panel", false)]
    [InlineData("compose", false)]
    [InlineData("panel", true)]
    [InlineData("compose", true)]
    public async Task CrashAfterUploadBeforeCheckpoint_RecoversImmutableArtifactWithoutProvider(string operation, bool canceledSave)
    {
        var h = new Harness();
        h.Attempt();
        if (operation == "compose")
        {
            await h.RunAsync();
            h.Select();
        }
        if (canceledSave)
            h.BeforeSave = document =>
            {
                if (document.PendingCommandId is null) throw new OperationCanceledException("Uncertain write");
            };
        else
            h.FailSave = document => document.PendingCommandId is null;
        await Assert.ThrowsAsync<Worker.PanelCommandCheckpointException>(() => h.RunAsync());
        Assert.NotNull(h.Saved.PendingCommandId);
        Assert.NotNull(h.Saved.CommandExecutionId);
        var calls = h.Provider.ImageCalls;
        var uploads = h.Blobs.Uploads;
        var bytes = h.Blobs.Content[h.CommandPath(operation == "panel" ? "panel-3" : "board")].ToArray();
        h.FailSave = null;
        h.BeforeSave = null;
        h.ExpireClaim();
        await h.RunAsync();
        Assert.Equal(calls, h.Provider.ImageCalls);
        Assert.Equal(uploads, h.Blobs.Uploads);
        Assert.Equal(bytes, h.Blobs.Content[h.CommandPath(operation == "panel" ? "panel-3" : "board")].ToArray());
        Assert.Equal(GenerationStatus.Completed, h.Saved.Status);
        Assert.Null(h.Saved.PendingCommandId);
        Assert.Equal("completed", h.Saved.Attempts.Last().Status);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{}")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":[{}]}""")]
    [InlineData("""{"data":[{"b64_json":"not base64"}]}""")]
    [InlineData("""{"data":[{"b64_json":"AQID"}]}""")]
    public async Task MalformedOrIncompleteImage_FailsOnlyCandidate(string body)
    {
        var h = new Harness();
        h.Attempt();
        var selected = h.Projection();
        h.Provider.Response = (_, _) => Json(body);
        await h.RunAsync();
        Assert.Equal(PanelGenerationStatus.Failed, h.Candidate.Status);
        Assert.Equal(PanelGenerationIssue.TechnicalFailure, h.Candidate.Issue);
        Assert.Null(h.Candidate.ImageBlobName);
        Assert.Equal(selected, h.Projection());
        Assert.Equal(0, h.Blobs.Uploads);
        Assert.Equal(1, h.Provider.ImageCalls);
        Assert.Equal(GenerationStatus.Completed, h.Saved.Status);
    }

    [Theory]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":[{"b64_json":"not base64"}]}""")]
    [InlineData("""{"data":[{"b64_json":"AQID"}]}""")]
    public async Task MalformedReferenceGuidedImage_FailsOnlyCandidate(string body)
    {
        var h = new Harness(withReference: true);
        h.Attempt();
        var selected = h.Projection();
        h.Provider.Response = (_, _) => Json(body);
        await h.RunAsync();
        Assert.Equal(PanelGenerationStatus.Failed, h.Candidate.Status);
        Assert.Equal(PanelGenerationIssue.TechnicalFailure, h.Candidate.Issue);
        Assert.Null(h.Candidate.ImageBlobName);
        Assert.Equal(selected, h.Projection());
        Assert.Equal(0, h.Blobs.Uploads);
        Assert.Equal(1, h.Provider.ImageCalls);
    }

    [Fact]
    public async Task ContentSafetyRejection_IsTerminal_WithoutEvasiveRewrite()
    {
        var h = new Harness();
        h.Attempt();
        var selected = h.Projection();
        h.Provider.Response = (_, _) => Json(
            """{"error":{"code":"content_safety_violation","message":"private response"}}""", 400);
        await h.RunAsync();
        Assert.Equal(PanelGenerationIssue.ContentSafetyRejected, h.Candidate.Issue);
        Assert.Equal(PanelGenerationStatus.Failed, h.Candidate.Status);
        Assert.Equal(1, h.Provider.ImageCalls);
        Assert.Equal(0, h.Provider.RewriteCalls);
        Assert.Equal(selected, h.Projection());
        Assert.DoesNotContain("private response", Serialize(h.Saved));
    }

    [Fact]
    public async Task Throttle_CheckpointsBeforeRetry_AndKeepsIndependentNullableUsage()
    {
        var h = new Harness();
        h.Attempt();
        h.Provider.Response = (_, count) => count == 1
            ? Json("""{"error":{"code":"busy"}}""", 429)
            : Json(ImageResponse("""{"input_tokens":0,"total_tokens":19}"""));
        await h.RunAsync();
        Assert.Equal(2, h.Provider.ImageCalls);
        Assert.Contains(h.Checkpoints, doc => h.CandidateIn(doc).Issue == PanelGenerationIssue.ProviderThrottled);
        Assert.Null(h.Candidate.Issue);
        Assert.Equal(2, h.Candidate.Traces.Count);
        Assert.All(h.Candidate.Traces, trace => Assert.Equal(h.Saved.Attempts.Last().Id, trace.AttemptId));
        Assert.Null(h.Candidate.Traces[0].InputTokens);
        Assert.Equal(0, h.Candidate.Traces[1].InputTokens);
        Assert.Null(h.Candidate.Traces[1].OutputTokens);
        Assert.Equal(19, h.Candidate.Traces[1].TotalTokens);
    }

    [Fact]
    public async Task ExhaustedTransientFailure_IsSavedOnCandidateOnly()
    {
        var h = new Harness();
        h.Attempt();
        var projection = h.Projection();
        h.Provider.Response = (_, _) => Json("""{"error":{"code":"unavailable"}}""", 503);
        await h.RunAsync();
        Assert.Equal(2, h.Provider.ImageCalls);
        Assert.Equal(2, h.Candidate.Traces.Count);
        Assert.All(h.Candidate.Traces, trace => Assert.Equal("failed", trace.Status));
        Assert.Equal(PanelGenerationStatus.Failed, h.Candidate.Status);
        Assert.Equal(PanelGenerationIssue.TechnicalFailure, h.Candidate.Issue);
        Assert.Equal(projection, h.Projection());
    }

    [Fact]
    public async Task ProviderTimeoutWithoutArtifact_CanRetryButCannotPromiseSingleAcceptance()
    {
        var h = new Harness();
        h.Attempt();
        h.Provider.Response = (_, count) => count == 1
            ? throw new TimeoutException("Provider acceptance is uncertain")
            : Json(ImageResponse());
        await h.RunAsync();
        Assert.Equal(2, h.Provider.ImageCalls);
        Assert.Equal(2, h.Candidate.Traces.Count);
        Assert.Equal("failed", h.Candidate.Traces[0].Status);
        Assert.Equal("completed", h.Candidate.Traces[1].Status);
        Assert.Equal(PanelGenerationStatus.Completed, h.Candidate.Status);
    }

    [Fact]
    public async Task Retry_ReusesFrozenEffectivePrompt_WithoutRewrite()
    {
        var h = new Harness();
        h.Saved.Panels[2].Status = PanelGenerationStatus.Failed;
        h.Saved.Panels[2].ImageBlobName = null;
        h.Saved.Panels[2].EffectivePrompt = "Frozen prepared scene";
        h.Attempt(retry: true);
        await h.RunAsync();
        Assert.Equal(0, h.Provider.RewriteCalls);
        Assert.Contains("Frozen prepared scene", Assert.Single(h.Provider.Requests));
        Assert.Equal("Frozen prepared scene", h.Candidate.EffectivePrompt);
        Assert.Equal(PanelGenerationStatus.Failed, h.Saved.Panels[2].Status);
        Assert.Null(h.Saved.Panels[2].ImageBlobName);
    }

    [Fact]
    public async Task RetryWithoutPreparedPrompt_RewritesFrozenInput_AndCheckpointsBeforeImage()
    {
        var h = new Harness();
        h.Saved.Panels[2].Status = PanelGenerationStatus.Failed;
        h.Saved.Panels[2].ImageBlobName = null;
        h.Saved.Panels[2].EffectivePrompt = null;
        h.Attempt(retry: true);
        h.Provider.Response = (rewrite, _) =>
        {
            if (!rewrite)
                Assert.Contains(h.Checkpoints, doc => h.CandidateIn(doc).EffectivePrompt == "Prepared frozen scene");
            return rewrite ? Json(ChatResponse("Prepared frozen scene")) : Json(ImageResponse());
        };
        await h.RunAsync();
        Assert.Equal(1, h.Provider.RewriteCalls);
        Assert.Equal(1, h.Provider.ImageCalls);
        Assert.Contains("Frozen context", h.Provider.Requests[0]);
        Assert.Contains("Original 3", h.Provider.Requests[0]);
        Assert.Equal("Prepared frozen scene", h.Candidate.EffectivePrompt);
        Assert.Equal(2, h.Candidate.Traces.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyRewrite_FailsCandidateWithoutImage(string rewritten)
    {
        var h = new Harness();
        h.Saved.Panels[2].Status = PanelGenerationStatus.Failed;
        h.Saved.Panels[2].ImageBlobName = null;
        h.Saved.Panels[2].EffectivePrompt = null;
        h.Attempt(retry: true);
        h.Provider.Response = (_, _) => Json(ChatResponse(rewritten));
        await h.RunAsync();
        Assert.Equal(1, h.Provider.RewriteCalls);
        Assert.Equal(0, h.Provider.ImageCalls);
        Assert.Equal(PanelGenerationStatus.Failed, h.Candidate.Status);
        Assert.Equal(PanelGenerationIssue.TechnicalFailure, h.Candidate.Issue);
    }

    [Fact]
    public async Task RewriteSafetyRejection_IsSavedWithoutFurtherRewriteOrImageCalls()
    {
        var h = new Harness();
        h.Saved.Panels[2].Status = PanelGenerationStatus.Failed;
        h.Saved.Panels[2].ImageBlobName = null;
        h.Saved.Panels[2].EffectivePrompt = null;
        h.Attempt(retry: true);
        h.Provider.Response = (_, _) => Json(
            """{"error":{"code":"content_safety_violation","message":"private response"}}""", 400);
        await h.RunAsync();
        Assert.Equal(1, h.Provider.RewriteCalls);
        Assert.Equal(0, h.Provider.ImageCalls);
        Assert.Equal(PanelGenerationIssue.ContentSafetyRejected, h.Candidate.Issue);
        Assert.Equal(PanelGenerationStatus.Failed, h.Candidate.Status);
        Assert.DoesNotContain("private response", Serialize(h.Saved));
    }

    [Fact]
    public async Task UsesRetainedReferencesAndFrozenIdentity_AfterProjectChanges()
    {
        var h = new Harness(withReference: true);
        h.Attempt();
        h.Project.Characters.Clear();
        h.Project.Context = "Changed context";
        await h.RunAsync();
        Assert.Contains("Frozen identity", Assert.Single(h.Provider.Requests));
        Assert.Contains("frozen-image", h.Provider.Requests[0]);
        Assert.DoesNotContain("Changed", h.Provider.Requests[0]);
        Assert.Equal(PanelGenerationStatus.Completed, h.Candidate.Status);
        Assert.Contains(h.Blobs.Reads, name => name.Contains("/input-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingSelectedSibling_FailsCompositionWithoutRegeneration_AndRetainsLastBoard()
    {
        var h = new Harness();
        h.Attempt();
        await h.RunAsync();
        h.Select();
        var previousBoard = h.Saved.BoardBlobName;
        h.Blobs.Content.Remove(h.Saved.Panels[0].ImageBlobName!);
        await h.RunAsync();
        Assert.Equal(1, h.Provider.ImageCalls);
        Assert.Equal(previousBoard, h.Saved.BoardBlobName);
        Assert.Equal(0, h.Saved.ComposedRevision);
        Assert.Equal(GenerationStatus.Failed, h.Saved.Status);
        Assert.Null(h.Saved.PendingCommandId);
        Assert.Equal(h.Candidate.Id, h.Saved.Panels[2].SelectedVersionId);
    }

    [Fact]
    public async Task StaleCompositionETag_CannotPublishBoardOrClearNewerCommand()
    {
        var h = new Harness();
        h.Attempt();
        await h.RunAsync();
        h.Select();
        var previousBoard = h.Saved.BoardBlobName;
        var newerCommand = Guid.NewGuid().ToString("D");
        h.Blobs.BeforeUpload = _ =>
        {
            h.Saved.Revision++;
            h.Saved.PendingCommandId = newerCommand;
            h.BumpETag();
        };
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => h.RunAsync());
        Assert.Equal(previousBoard, h.Saved.BoardBlobName);
        Assert.Equal(0, h.Saved.ComposedRevision);
        Assert.Equal(newerCommand, h.Saved.PendingCommandId);
        Assert.Equal(1, h.Provider.ImageCalls);
    }

    [Theory]
    [InlineData(409)]
    [InlineData(412)]
    public async Task UploadRace_UsesFirstArtifact_WithoutOverwrite(int status)
    {
        var h = new Harness();
        h.Attempt();
        var first = Png(37);
        h.Blobs.BeforeUpload = name =>
        {
            h.Blobs.Content[name] = BinaryData.FromBytes(first);
            throw new RequestFailedException(status, "Already exists");
        };
        await h.RunAsync();
        Assert.Equal(first, h.Blobs.Content[h.CommandPath("panel-3")].ToArray());
        Assert.Equal(PanelGenerationStatus.Completed, h.Candidate.Status);
        Assert.Equal(1, h.Provider.ImageCalls);
    }

    [Fact]
    public async Task ActiveDuplicate_ThrowsBusy_WithoutWritesOrProviderCalls()
    {
        var h = new Harness();
        h.Attempt();
        PanelVersionCommands.Claim(h.Saved, DateTimeOffset.UtcNow);
        var json = Serialize(h.Saved);
        var busy = await Assert.ThrowsAsync<Worker.PanelCommandBusyException>(() => h.RunAsync());
        Assert.Equal(h.Saved.CommandLeaseExpiresAt, busy.RetryAt);
        Assert.Equal(json, Serialize(h.Saved));
        Assert.Empty(h.Checkpoints);
        Assert.Equal(0, h.Provider.ImageCalls);
    }

    [Fact]
    public async Task OverlappingDeliveries_OnlyClaimantInvokesProvider()
    {
        var h = new Harness();
        h.Attempt();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Provider.BeforeResponseAsync = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var first = h.RunAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<Worker.PanelCommandBusyException>(() => h.RunAsync());
            Assert.Equal(1, h.Provider.ImageCalls);
        }
        finally
        {
            release.TrySetResult();
            await first;
        }
        Assert.Equal(PanelGenerationStatus.Completed, h.Candidate.Status);
        Assert.Single(h.Saved.Attempts);
    }

    [Fact]
    public async Task ExpiredClaim_CanBeTakenOver()
    {
        var h = new Harness();
        h.Attempt();
        var old = PanelVersionCommands.Claim(h.Saved, DateTimeOffset.UtcNow - TimeSpan.FromHours(1));
        h.Saved.Attempts.Add(new() { Id = old, StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1) });
        await h.RunAsync();
        Assert.Equal(2, h.Saved.Attempts.Count);
        Assert.NotEqual(old, h.Saved.Attempts.Last().Id);
        Assert.Equal(PanelGenerationStatus.Completed, h.Candidate.Status);
    }

    [Fact]
    public async Task ClaimETagConflict_NeverCallsProviderOrWritesFailure()
    {
        var h = new Harness();
        h.Attempt();
        h.BeforeSave = _ =>
        {
            h.BeforeSave = null;
            PanelVersionCommands.Claim(h.Saved, DateTimeOffset.UtcNow);
            h.BumpETag();
        };
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => h.RunAsync());
        Assert.Equal(0, h.Provider.ImageCalls);
        Assert.Empty(h.Checkpoints);
        Assert.NotNull(h.Saved.CommandExecutionId);
        Assert.Equal(PanelGenerationStatus.Pending, h.Candidate.Status);
    }

    [Fact]
    public async Task StaleETagAfterProvider_NeverPublishesOrWritesStaleFailure()
    {
        var h = new Harness();
        h.Attempt();
        var selected = h.Projection();
        h.Provider.Response = (_, _) =>
        {
            PanelVersionCommands.Claim(h.Saved, DateTimeOffset.UtcNow + TimeSpan.FromHours(1));
            h.BumpETag();
            return Json(ImageResponse());
        };
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => h.RunAsync());
        Assert.Equal(selected, h.Projection());
        Assert.Equal(PanelGenerationStatus.Processing, h.Candidate.Status);
        Assert.NotNull(h.Saved.PendingCommandId);
        Assert.NotNull(h.Saved.CommandExecutionId);
        Assert.Equal(1, h.Provider.ImageCalls);
        Assert.DoesNotContain(h.Checkpoints, doc => doc.PendingCommandId is null);
    }

    [Fact]
    public async Task ExpiryDuringProvider_RejectsResultBeforeUploadOrCheckpoint()
    {
        var h = new Harness();
        h.Attempt();
        h.Provider.Response = (_, _) =>
        {
            h.Running!.CommandLeaseExpiresAt = h.Clock.GetUtcNow().AddSeconds(-1);
            return Json(ImageResponse());
        };
        await Assert.ThrowsAsync<Worker.PanelCommandLostException>(() => h.RunAsync());
        Assert.Equal(0, h.Blobs.Uploads);
        Assert.NotNull(h.Saved.PendingCommandId);
        Assert.Equal(PanelGenerationStatus.Processing, h.Candidate.Status);
    }

    [Fact]
    public async Task CancellationAtProviderAcceptanceBoundary_IsRecoverableButMayCallProviderAgain()
    {
        var h = new Harness();
        h.Attempt();
        var projection = h.Projection();
        using var cancellation = new CancellationTokenSource();
        h.Provider.Response = (_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.RunAsync(cancellation.Token));
        Assert.Equal(1, h.Provider.ImageCalls);
        Assert.Equal(GenerationStatus.Pending, h.Saved.Status);
        Assert.Null(h.Saved.CommandExecutionId);
        Assert.Null(h.Saved.CommandLeaseExpiresAt);
        Assert.NotNull(h.Saved.PendingCommandId);
        Assert.Equal(PanelGenerationStatus.Pending, h.Candidate.Status);
        Assert.Equal("canceled", h.Saved.Attempts.Last().Status);
        Assert.Equal(projection, h.Projection());
        h.Provider.Response = null;
        await h.RunAsync();
        // No provider idempotency is claimed when acceptance happened before any saved artifact.
        Assert.Equal(2, h.Provider.ImageCalls);
        Assert.Equal(2, h.Saved.Attempts.Count);
        Assert.Equal(PanelGenerationStatus.Completed, h.Candidate.Status);
    }

    [Fact]
    public async Task FixedExecutionDeadline_CancelsAt25Minutes_AndReleasesBefore30MinuteLease()
    {
        var h = new Harness();
        h.Attempt();
        var projection = h.Projection();
        h.Provider.BeforeResponseAsync = token =>
        {
            Assert.Equal(h.Clock.GetUtcNow() + PanelVersionCommands.LeaseDuration, h.Saved.CommandLeaseExpiresAt);
            h.Clock.Advance(PanelVersionCommands.ExecutionTimeout);
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.RunAsync());
        Assert.Equal(1, h.Provider.ImageCalls);
        Assert.Equal(GenerationStatus.Pending, h.Saved.Status);
        Assert.NotNull(h.Saved.PendingCommandId);
        Assert.Null(h.Saved.CommandExecutionId);
        Assert.Null(h.Saved.CommandLeaseExpiresAt);
        Assert.Equal(PanelGenerationStatus.Pending, h.Candidate.Status);
        Assert.Equal("canceled", h.Saved.Attempts.Last().Status);
        Assert.Equal("canceled", Assert.Single(h.Candidate.Traces).Status);
        Assert.Equal(projection, h.Projection());
    }

    [Theory]
    [InlineData("panel")]
    [InlineData("compose")]
    public async Task CancellationAfterUploadBeforeCheckpoint_ReleasesClaimAndReusesArtifact(string operation)
    {
        var h = new Harness();
        h.Attempt();
        if (operation == "compose")
        {
            await h.RunAsync();
            h.Select();
        }
        var projection = h.Projection();
        using var cancellation = new CancellationTokenSource();
        h.Blobs.AfterUpload = () => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.RunAsync(cancellation.Token));
        Assert.Equal(GenerationStatus.Pending, h.Saved.Status);
        Assert.NotNull(h.Saved.PendingCommandId);
        Assert.Null(h.Saved.CommandExecutionId);
        Assert.Equal(projection, h.Projection());
        var calls = h.Provider.ImageCalls;
        var uploads = h.Blobs.Uploads;
        h.Blobs.AfterUpload = null;
        await h.RunAsync();
        Assert.Equal(calls, h.Provider.ImageCalls);
        Assert.Equal(uploads, h.Blobs.Uploads);
        Assert.Equal(GenerationStatus.Completed, h.Saved.Status);
        Assert.Null(h.Saved.PendingCommandId);
    }

    [Fact]
    public async Task CancellationDuringThrottleBackoff_ReleasesOnlyOwnClaim()
    {
        var h = new Harness();
        h.Attempt();
        using var cancellation = new CancellationTokenSource();
        h.Provider.Response = (_, _) => Json("""{"error":{"code":"busy"}}""", 429);
        h.AfterSave = doc =>
        {
            if (h.CandidateIn(doc).Issue == PanelGenerationIssue.ProviderThrottled)
                cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.RunAsync(cancellation.Token));
        Assert.Equal(1, h.Provider.ImageCalls);
        Assert.Null(h.Saved.CommandExecutionId);
        Assert.Equal(PanelGenerationStatus.Pending, h.Candidate.Status);
        Assert.Null(h.Candidate.Issue);
        Assert.NotNull(h.Saved.PendingCommandId);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LegacyDeliveriesCannotOverwritePanelCommandHistory(int schema)
    {
        var h = new Harness();
        h.Attempt();
        h.Saved.Status = GenerationStatus.Failed;
        h.Message = h.Message with { SchemaVersion = schema };
        var json = Serialize(h.Saved);
        await h.RunAsync();
        Assert.Equal(json, Serialize(h.Saved));
        Assert.Equal(0, h.Provider.ImageCalls);
        Assert.Equal(0, h.Blobs.Uploads);
        Assert.Empty(h.Checkpoints);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("revision")]
    [InlineData("deleted")]
    public async Task ObsoleteOrDeletedCommand_IsNoOp(string reason)
    {
        var h = new Harness();
        h.Attempt();
        if (reason == "command") h.Message = h.Message with { CommandId = Guid.NewGuid().ToString("D") };
        if (reason == "revision") h.Message = h.Message with { Revision = h.Saved.Revision + 1 };
        if (reason == "deleted") h.Project.DeletionRequestedAt = DateTimeOffset.UtcNow;
        await h.RunAsync();
        Assert.Empty(h.Checkpoints);
        Assert.Equal(0, h.Provider.ImageCalls);
        Assert.Equal(0, h.Blobs.Uploads);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("operation")]
    [InlineData("panel")]
    [InlineData("revision")]
    [InlineData("command")]
    public async Task InvalidSchemaFourMessage_IsRejectedBeforeClaim(string reason)
    {
        var h = new Harness();
        h.Attempt();
        h.Message = reason switch
        {
            "run" => h.Message with { RunId = Guid.NewGuid().ToString("D") },
            "operation" => h.Message with { Operation = "unknown" },
            "panel" => h.Message with { PanelNumber = null },
            "revision" => h.Message with { Revision = null },
            _ => h.Message with { CommandId = Guid.Empty.ToString("D") }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => h.RunAsync());
        Assert.Empty(h.Checkpoints);
        Assert.Equal(0, h.Provider.ImageCalls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("layout")]
    [InlineData("foreign-path")]
    [InlineData("corrupt-reference")]
    public async Task InvalidFrozenSnapshot_FailsCandidateWithoutLiveFallback(string reason)
    {
        var h = new Harness(withReference: true);
        h.Attempt();
        var selected = h.Projection();
        var node = System.Text.Json.Nodes.JsonNode.Parse(Serialize(h.Saved.InputSnapshot))!;
        if (reason == "version") node["version"] = 99;
        if (reason == "layout") node["layoutVersion"] = "unsupported";
        if (reason == "foreign-path") node["characters"]![0]!["references"]![0]!["blobName"] = "other-owner/input";
        if (reason == "corrupt-reference")
            h.Blobs.Content[h.Saved.InputSnapshot!.Characters[0].References[0].BlobName] = BinaryData.FromBytes([9]);
        h.Saved.InputSnapshot = reason == "missing" ? null :
            JsonSerializer.Deserialize<GenerationInputSnapshot>(node.ToJsonString(), JsonOptions);
        await h.RunAsync();
        Assert.Equal(0, h.Provider.ImageCalls);
        Assert.Equal(0, h.Provider.RewriteCalls);
        Assert.Equal(PanelGenerationStatus.Failed, h.Candidate.Status);
        Assert.Equal(PanelGenerationIssue.TechnicalFailure, h.Candidate.Issue);
        Assert.Equal(selected, h.Projection());
        Assert.Equal(GenerationStatus.Completed, h.Saved.Status);
        Assert.Equal(0, h.Blobs.Uploads);
    }

    [Fact]
    public async Task InFlightFullBoard_CannotOverwriteOriginalArtworkAfterPanelEditingBegins()
    {
        var h = new Harness();
        h.Saved.Status = GenerationStatus.Pending;
        h.Saved.Panels[2].Status = PanelGenerationStatus.Pending;
        var originalPath = h.Saved.Panels[2].ImageBlobName!;
        var originalBytes = Png(100);
        h.Blobs.Content[originalPath] = BinaryData.FromBytes(originalBytes);
        h.Message = new()
        {
            SchemaVersion = 3, BoardId = h.Saved.Id, RunId = h.Saved.Id,
            OwnerId = h.Saved.OwnerId, ProjectId = h.Saved.ProjectId
        };
        h.Provider.Response = (_, _) =>
        {
            // A competing delivery finishes and the API accepts a targeted command
            // while this execution's provider request is in flight.
            h.Saved.Status = GenerationStatus.Completed;
            h.Saved.Panels[2].Status = PanelGenerationStatus.Completed;
            h.Attempt();
            h.BumpETag();
            return Json(ImageResponse());
        };

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => h.RunAsync());

        Assert.Equal(originalBytes, h.Blobs.Content[originalPath].ToArray());
        Assert.Equal(originalPath, h.Saved.Panels[2].Versions[0].ImageBlobName);
        Assert.Equal(GenerationStatus.Pending, h.Saved.Status);
        Assert.Equal(h.Candidate.Id, h.Saved.PendingCommandId);
        Assert.Equal(0, h.Blobs.Uploads);
    }

    [Fact]
    public async Task InFlightFullBoard_CannotOverwritePriorBoardAfterPanelEditingBegins()
    {
        var h = new Harness();
        h.Saved.Status = GenerationStatus.Pending;
        var originalPath = h.Saved.BoardBlobName!;
        var originalBytes = h.Blobs.Content[originalPath].ToArray();
        h.Message = new()
        {
            SchemaVersion = 3, BoardId = h.Saved.Id, RunId = h.Saved.Id,
            OwnerId = h.Saved.OwnerId, ProjectId = h.Saved.ProjectId
        };
        h.Blobs.BeforeUpload = _ =>
        {
            h.Saved.Status = GenerationStatus.Completed;
            h.Attempt();
            h.BumpETag();
        };

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => h.RunAsync());

        Assert.Equal(originalBytes, h.Blobs.Content[originalPath].ToArray());
        Assert.Equal(originalPath, h.Saved.BoardBlobName);
        Assert.Equal(h.Candidate.Id, h.Saved.PendingCommandId);
        Assert.Equal(1, h.Blobs.Uploads);
        Assert.Contains(h.Blobs.Content.Keys, name => name.EndsWith(
            $"initial-board-{Guid.Parse(h.Running!.Attempts.Last().Id):N}.png", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectionCommandUsingInitialAttemptId_CannotRecoverTheInitialBoard()
    {
        var h = new Harness();
        h.Saved.Status = GenerationStatus.Pending;
        h.Message = new()
        {
            SchemaVersion = 3, BoardId = h.Saved.Id, RunId = h.Saved.Id,
            OwnerId = h.Saved.OwnerId, ProjectId = h.Saved.ProjectId
        };
        await h.RunAsync();
        var initialAttemptId = Guid.Parse(Assert.Single(h.Saved.Attempts).Id);
        var initialBoardPath = h.Saved.BoardBlobName!;
        var initialBoardBytes = h.Blobs.Content[initialBoardPath].ToArray();
        h.Attempt();
        h.Provider.Response = (_, _) =>
            Json($$"""{"data":[{"b64_json":"{{Convert.ToBase64String(Png(200))}}"}]}""");
        await h.RunAsync();
        PanelVersionCommands.SelectVersion(h.Saved, 3, initialAttemptId, Guid.Parse(h.Candidate.Id),
            h.Saved.Revision, DateTimeOffset.UtcNow);
        h.Message = h.Message with
        {
            Operation = "compose", CommandId = initialAttemptId.ToString("D"), Revision = h.Saved.Revision
        };
        var uploads = h.Blobs.Uploads;

        await h.RunAsync();

        Assert.Equal(uploads + 1, h.Blobs.Uploads);
        Assert.Equal(h.CommandPath("board"), h.Saved.BoardBlobName);
        Assert.NotEqual(initialBoardPath, h.Saved.BoardBlobName);
        Assert.False(initialBoardBytes.SequenceEqual(h.Blobs.Content[h.Saved.BoardBlobName!].ToArray()));
        Assert.Equal(initialBoardBytes, h.Blobs.Content[initialBoardPath].ToArray());
    }

    [Fact]
    public async Task Wakeup_UsesDistinctDeterministicTransportIdentity_AndExpiryMargin()
    {
        var sender = new Queue();
        var command = Guid.NewGuid().ToString("D");
        var delivery = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("""{"commandId":"business"}"""), messageId: command);
        var expiry = DateTimeOffset.UtcNow.AddMinutes(30);
        await Worker.PanelCommandWakeup.ScheduleAsync(sender, delivery, command, expiry, CancellationToken.None);
        await Worker.PanelCommandWakeup.ScheduleAsync(sender, delivery, command, expiry, CancellationToken.None);
        Assert.Equal($"{command}:wake:{expiry.UtcTicks}", sender.Messages[0].MessageId);
        Assert.Equal(sender.Messages[0].MessageId, sender.Messages[1].MessageId);
        Assert.NotEqual(delivery.MessageId, sender.Messages[0].MessageId);
        Assert.Equal(delivery.Body.ToString(), sender.Messages[0].Body.ToString());
        Assert.All(sender.Scheduled, time => Assert.Equal(expiry.AddSeconds(5), time));
    }

    [Fact]
    public async Task WakeupSchedulingFailure_PropagatesForNormalAbandonment()
    {
        var sender = new Queue { FailSchedule = true };
        var delivery = ServiceBusModelFactory.ServiceBusReceivedMessage(body: BinaryData.FromString("{}"));
        await Assert.ThrowsAsync<ServiceBusException>(() => Worker.PanelCommandWakeup.ScheduleAsync(
            sender, delivery, Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow.AddMinutes(30), CancellationToken.None));
        Assert.Empty(sender.Messages);
    }

    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(Serialize(value), JsonOptions)!;
    private static byte[] Png(byte red = 1)
    {
        using var image = new Image<Rgba32>(2, 2, new Rgba32(red, 2, 3));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }
    private static string ImageResponse(string? usage = null) =>
        $$"""{"data":[{"b64_json":"{{Convert.ToBase64String(Png())}}"}]{{(usage is null ? "" : ",\"usage\":" + usage)}}}""";
    private static string ChatResponse(string text) => Serialize(new
    {
        id = "safe-chat-id", model = "frozen-text", created = 1, @object = "chat.completion",
        choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = text } } },
        usage = new { prompt_tokens = 0, completion_tokens = 5, total_tokens = 5 }
    });
    private static HttpResponseMessage Json(string body, int status = 200)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation("retry-after-ms", "1");
        response.Headers.TryAddWithoutValidation("x-request-id", "safe-request-id");
        return response;
    }

    private sealed class Harness
    {
        public GenerationDocument Saved { get; set; }
        public GenerationDocument? Running { get; private set; }
        public ProjectDocument Project { get; } = new()
        {
            Id = Guid.NewGuid().ToString("D"), OwnerId = "owner-a", Context = "Live context"
        };
        public BoardGenerationRequestMessage Message { get; set; } = new();
        public Blobs Blobs { get; } = new();
        public Provider Provider { get; } = new();
        public Queue Started { get; } = new();
        public Queue Completed { get; } = new();
        public ManualClock Clock { get; } = new();
        public List<GenerationDocument> Checkpoints { get; } = [];
        public Func<GenerationDocument, bool>? FailSave { get; set; }
        public Action<GenerationDocument>? BeforeSave { get; set; }
        public Action<GenerationDocument>? AfterSave { get; set; }
        private string candidateId = string.Empty;
        public PanelVersion Candidate => CandidateIn(Saved);
        public PanelVersion CandidateIn(GenerationDocument document) =>
            document.Panels[2].Versions.Single(version => version.Id == candidateId);

        public Harness(bool withReference = false)
        {
            var id = Guid.NewGuid().ToString("D");
            var snapshot = new GenerationInputSnapshot
            {
                RunId = id, Title = "Frozen title", Context = "Frozen context", RewritePrompt = true,
                Template = BoardTemplate.Standard, PromptTemplateVersion = BoardInputVersions.PromptTemplate,
                LayoutVersion = BoardInputVersions.Layout,
                Settings = new()
                {
                    PromptDeployment = "frozen-text", ImageDeployment = "frozen-image", ImageSize = 1024,
                    MaxParallelImages = 3, ImageRetryCount = 1, ImageRetryBaseDelayMilliseconds = 1
                },
                Panels = Enumerable.Range(1, 6).Select(n => new GenerationInputPanel
                {
                    Number = n, Prompt = $"Original {n}", CharacterIds = withReference && n == 3 ? ["hero"] : []
                }).ToList()
            };
            Saved = new()
            {
                Id = id, OwnerId = Project.OwnerId, ProjectId = Project.Id, SchemaVersion = 4,
                InputSnapshot = snapshot, Title = "Live title", Status = GenerationStatus.Completed,
                ETag = "1", CreatedAt = DateTimeOffset.UtcNow,
                Panels = Enumerable.Range(1, 6).Select(n => new GenerationPanel
                {
                    Number = n, Prompt = $"Original {n}", EffectivePrompt = $"Prepared original {n}",
                    Status = PanelGenerationStatus.Completed, ImageBlobName = Path($"panel-{n:00}.png")
                }).ToList(),
                BoardBlobName = Path("board.png")
            };
            foreach (var panel in Saved.Panels) Blobs.Content[panel.ImageBlobName!] = BinaryData.FromBytes(Png());
            Blobs.Content[Saved.BoardBlobName] = BinaryData.FromBytes(Png());
            if (withReference)
            {
                var bytes = Png();
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var reference = new GenerationInputReference
                {
                    Id = "retained-reference", ContentSha256 = hash, BlobName = Path($"input-retained-reference-{hash}"),
                    ContentType = "image/png", FileName = "synthetic.png"
                };
                snapshot.Characters.Add(new() { Id = "hero", Name = "Frozen identity", References = [reference] });
                Blobs.Content[reference.BlobName] = BinaryData.FromBytes(bytes);
            }
            string Path(string name) => Worker.ArtifactPathBuilder.GetGenerationBlobName(Project.OwnerId, Guid.Parse(id), name);
        }

        public void Attempt(bool retry = false)
        {
            var id = Guid.NewGuid();
            PanelVersionCommands.CreateAttempt(Saved, 3, id, Saved.Revision, retry,
                retry ? null : "Literal revised scene", null, DateTimeOffset.UtcNow);
            candidateId = id.ToString("D");
            SetMessage();
        }

        public void Select()
        {
            PanelVersionCommands.SelectVersion(Saved, 3, Guid.NewGuid(), Guid.Parse(Candidate.Id),
                Saved.Revision, DateTimeOffset.UtcNow);
            SetMessage();
        }

        private void SetMessage() => Message = new()
        {
            SchemaVersion = 4, BoardId = Saved.Id, RunId = Saved.Id, ProjectId = Saved.ProjectId,
            OwnerId = Saved.OwnerId, Operation = Saved.PendingCommandKind, CommandId = Saved.PendingCommandId,
            PanelNumber = Saved.PendingPanelNumber, Revision = Saved.Revision
        };

        public string CommandPath(string prefix) => Worker.ArtifactPathBuilder.GetGenerationBlobName(
            Saved.OwnerId, Guid.Parse(Saved.Id), $"{prefix}-{Guid.Parse(Message.CommandId!):N}.png");
        public void BumpETag() => Saved.ETag = (int.Parse(Saved.ETag!) + 1).ToString();
        public void ExpireClaim()
        {
            Saved.CommandLeaseExpiresAt = Clock.GetUtcNow().AddSeconds(-1);
            BumpETag();
        }
        public string Projection(bool includeBoard = true) => Serialize(new
        {
            Panels = Saved.Panels.Select(p => new
            {
                p.Prompt, p.EffectivePrompt, p.Status, p.ImageBlobName, p.SelectedVersionId, p.Issue, p.Error, p.Traces
            }),
            Board = includeBoard ? Saved.BoardBlobName : null
        });

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            Running = Copy(Saved);
            await using var db = new MaiImageGeneratorDbContext(
                new DbContextOptionsBuilder<MaiImageGeneratorDbContext>()
                    .UseCosmos("https://localhost:8081", Convert.ToBase64String(new byte[64]), "panel-worker-tests")
                    .AddInterceptors(new Saves(this)).Options);
            var document = Running;
            db.Generations.Attach(document);
            using var http = new HttpClient(Provider);
            var openAI = new OpenAIClient(new ApiKeyCredential("unused-test-value"), new OpenAIClientOptions
            {
                Endpoint = new Uri("https://example.test/openai/v1/"),
                Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0)
            });
            var live = new FoundryOptions
            {
                Endpoint = "https://example.test", ImageModel = "changed-image", PromptModel = "changed-text"
            };
            var worker = new Worker.GenerationService(db, new Worker.ReferenceService(db, Blobs), Blobs,
                new Worker.FoundryImageClient(openAI, openAI.GetImageClient("changed-image"), http,
                    new Credential(), Options.Create(live)),
                new Worker.ImageComposer(Options.Create(live)), new Worker.PanelGenerationNotifier(Started, Completed),
                new ImageGenerationCoordinator(live), NullLogger<Worker.GenerationService>.Instance, Clock);
            await (Task)typeof(Worker.GenerationService)
                .GetMethod("ProcessAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(worker, [Message, document, Project, cancellationToken])!;
        }

        private sealed class Saves(Harness h) : SaveChangesInterceptor
        {
            public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
                InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var db = eventData.Context!;
                db.ChangeTracker.DetectChanges();
                var document = db.Set<GenerationDocument>().Local.Single();
                h.BeforeSave?.Invoke(document);
                if (db.Entry(document).Property(p => p.ETag).OriginalValue != h.Saved.ETag)
                    throw new DbUpdateConcurrencyException("Deterministic ETag conflict");
                if (h.FailSave?.Invoke(document) == true) throw new IOException("Uncertain checkpoint");
                document.ETag = (int.Parse(h.Saved.ETag!) + 1).ToString();
                h.Saved = Copy(document);
                h.Checkpoints.Add(Copy(document));
                // Suppressed Cosmos writes do not run its ordinal-key store-generation stage.
                // Keep added owned entries tracked; only advance the simulated root ETag.
                db.Entry(document).Property(p => p.ETag).OriginalValue = document.ETag;
                h.AfterSave?.Invoke(document);
                return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(1));
            }
        }
    }

    private sealed class Provider : HttpMessageHandler
    {
        public int ImageCalls { get; private set; }
        public int RewriteCalls { get; private set; }
        public List<string> Requests { get; } = [];
        public Func<bool, int, HttpResponseMessage>? Response { get; set; }
        public Func<CancellationToken, Task>? BeforeResponseAsync { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            var rewrite = request.RequestUri!.AbsolutePath.Contains("/chat/", StringComparison.Ordinal);
            int callNumber;
            lock (Requests)
            {
                Requests.Add(body);
                callNumber = rewrite ? ++RewriteCalls : ++ImageCalls;
            }
            if (BeforeResponseAsync is not null) await BeforeResponseAsync(token);
            return Response?.Invoke(rewrite, callNumber) ??
                   Json(rewrite ? ChatResponse("Prepared frozen scene") : ImageResponse());
        }
        // A harness uses a fresh HttpClient per execution but deliberately reuses this double.
        protected override void Dispose(bool disposing) { }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        private readonly List<Timer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            now += duration;
            foreach (var timer in timers.ToArray()) timer.FireIfDue();
        }
        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? due;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed) return false;
                due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;
                return true;
            }
            public void FireIfDue()
            {
                if (disposed || due is null || due > clock.now) return;
                due = null;
                callback(state);
            }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken) =>
            new("unused-test-value", DateTimeOffset.UtcNow.AddMinutes(5));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(context, cancellationToken));
    }

    private sealed class Queue : ServiceBusSender
    {
        public List<ServiceBusMessage> Messages { get; } = [];
        public List<DateTimeOffset> Scheduled { get; } = [];
        public bool FailSchedule { get; init; }
        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
        public override Task<long> ScheduleMessageAsync(ServiceBusMessage message, DateTimeOffset scheduledEnqueueTime,
            CancellationToken cancellationToken = default)
        {
            if (FailSchedule) throw new ServiceBusException("Schedule unavailable", ServiceBusFailureReason.ServiceBusy);
            Messages.Add(message);
            Scheduled.Add(scheduledEnqueueTime);
            return Task.FromResult(1L);
        }
    }

    private sealed class Blobs : BlobServiceClient
    {
        public Dictionary<string, BinaryData> Content { get; } = [];
        public List<string> Reads { get; } = [];
        public int Uploads { get; private set; }
        public BlobUploadOptions? LastUpload { get; private set; }
        public Action<string>? BeforeUpload { get; set; }
        public Action? AfterUpload { get; set; }
        public override BlobContainerClient GetBlobContainerClient(string name) => new Container(this, name);
        private sealed class Container(Blobs blobs, string container) : BlobContainerClient
        {
            public override BlobClient GetBlobClient(string name)
            {
                Assert.Equal("generations", container);
                return new Blob(blobs, name);
            }
        }
        private sealed class Blob(Blobs blobs, string name) : BlobClient
        {
            public override Task<Response<BlobContentInfo>> UploadAsync(BinaryData content, bool overwrite = false,
                CancellationToken cancellationToken = default)
            {
                Assert.False(overwrite);
                return UploadAsync(content, new BlobUploadOptions
                {
                    Conditions = new() { IfNoneMatch = ETag.All },
                    HttpHeaders = new() { ContentType = "image/png" }
                }, cancellationToken);
            }
            public override Task<Response<BlobInfo>> SetHttpHeadersAsync(BlobHttpHeaders httpHeaders,
                BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default) =>
                Task.FromResult(Response.FromValue(BlobsModelFactory.BlobInfo(new ETag("test"), DateTimeOffset.UtcNow), null!));

            public override Task<Response<BlobDownloadResult>> DownloadContentAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                blobs.Reads.Add(name);
                return blobs.Content.TryGetValue(name, out var value)
                    ? Task.FromResult(Response.FromValue(BlobsModelFactory.BlobDownloadResult(value), null!))
                    : throw new RequestFailedException(404, "Not found");
            }
            public override Task<Response<BlobContentInfo>> UploadAsync(BinaryData content, BlobUploadOptions options,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Equal(ETag.All, options.Conditions.IfNoneMatch);
                Assert.Equal("image/png", options.HttpHeaders.ContentType);
                blobs.BeforeUpload?.Invoke(name);
                if (!blobs.Content.TryAdd(name, content)) throw new RequestFailedException(412, "Already exists");
                blobs.LastUpload = options;
                blobs.Uploads++;
                blobs.AfterUpload?.Invoke();
                return Task.FromResult(Response.FromValue(
                    BlobsModelFactory.BlobContentInfo(new ETag("test"), DateTimeOffset.UtcNow, [], null!, null!, 0), null!));
            }
        }
    }
}
