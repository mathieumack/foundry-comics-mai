using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using MaiImageGenerator.Web.Components;
using MaiImageGenerator.Web.Models;
using MaiImageGenerator.Web.Pages;
using MaiImageGenerator.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Radzen;

namespace MaiImageGenerator.Tests;

public sealed class PanelVersionEditorTests
{
    private static readonly Guid Alternative = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid FailedVersion = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-01-01T12:00:00Z");

    [Fact]
    public async Task LegacyResponse_DefaultsToReadOnlyWithoutAttemptActions()
    {
        var old = JsonSerializer.Deserialize<GenerationResponse>(
            JsonSerializer.Serialize(Board() with { CanEditPanels = false, Revision = 0 }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await using var harness = await Harness.OpenAsync(old);
        var html = await harness.HtmlAsync();
        Assert.Contains("ancienne planche", html);
        Assert.DoesNotContain("Créer une autre version", html);
        Assert.DoesNotContain("Réessayer la case", html);
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, true));
        Assert.Empty(harness.Commands);

        var minimal = JsonSerializer.Deserialize<GenerationResponse>(
            """{"generationId":"00000000-0000-0000-0000-000000000001","panels":[],"characters":[]}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(minimal.CanEditPanels);
        Assert.Equal(0, minimal.Revision);
    }

    [Fact]
    public async Task OriginalFailureWithoutHistory_RetriesFrozenInputsWithNoSource()
    {
        var response = Board(failed: true);
        await using var harness = await Harness.OpenAsync(response);
        Assert.Contains("Réessayer la case", await harness.HtmlAsync());
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, true));
        var command = Assert.Single(harness.Commands);
        Assert.Equal(HttpMethod.Post, command.Method);
        Assert.Equal($"/api/generations/{response.GenerationId:D}/panels/1/attempts", command.Path);
        var body = command.Json<CreatePanelAttemptRequest>();
        Assert.True(body.Retry);
        Assert.Equal(12, body.ExpectedRevision);
        Assert.NotEqual(Guid.Empty, body.AttemptId);
        Assert.Null(body.Prompt);
        Assert.Null(body.SourceVersionId);
    }

    [Fact]
    public async Task RevisedPromptInput_RendersActualPerSceneDraft_AndEmptyInputDisablesCreation()
    {
        await using var harness = await Harness.OpenAsync(Board());
        var empty = await harness.HtmlAsync();
        Assert.Matches("<button[^>]*disabled[^>]*>Cr", empty);
        Assert.DoesNotContain("RevisedPrompt(savedPanel.Number)", empty);
        await harness.RunAsync(() => harness.Home.Revise(1, "Ma première consigne"));
        Assert.Contains("Ma premi", await harness.HtmlAsync());
        await harness.RunAsync(() => harness.Home.Select(1));
        Assert.DoesNotContain("Ma premi", await harness.HtmlAsync());
        await harness.RunAsync(() => harness.Home.Revise(2, "Ma deuxième consigne"));
        Assert.Contains("Ma deuxi", await harness.HtmlAsync());
        await harness.RunAsync(() => harness.Home.Select(0));
        var restored = await harness.HtmlAsync();
        Assert.Contains("Ma premi", restored);
        Assert.DoesNotContain("Ma deuxi", restored);
        Assert.True(harness.Home.Dirty);
    }

    [Fact]
    public async Task DraftReordering_PreservesSceneAndTargetsSavedPanelIdentityDuringRefresh()
    {
        await using var harness = await Harness.OpenAsync(Board(failed: true));
        await harness.RunAsync(() =>
        {
            harness.Home.Revise(1, "Version de la première case sauvegardée");
            harness.Home.Move(1);
        });
        await harness.RunAsync(harness.Home.RefreshAsync);
        Assert.Equal(1, harness.Home.SelectedPanel);
        Assert.Equal("Original 1", harness.Home.DraftPrompt(1));
        Assert.Equal("Original 2", harness.Home.DraftPrompt(0));
        Assert.Contains("premi", await harness.HtmlAsync());
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, true));
        Assert.EndsWith("/panels/1/attempts", Assert.Single(harness.Commands).Path);
        Assert.True(harness.Home.Dirty);
    }

    [Fact]
    public async Task ConfirmedScaffoldReplacement_ClearsOldVersionCommandsAndHiddenRevisedDrafts()
    {
        await using var harness = await Harness.OpenAsync(Board());
        harness.CommandHandler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await harness.RunAsync(() => harness.Home.Revise(1, "Ancienne consigne révisée"));
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, false));
        Assert.Contains("même commande", await harness.HtmlAsync());
        await harness.RunAsync(harness.Home.ReplaceScaffold);
        Assert.Equal("", harness.Home.Revised(1));
        Assert.DoesNotContain("même commande", await harness.HtmlAsync());
        Assert.DoesNotContain("Envoi non confirm", await harness.HtmlAsync());
        await harness.RunAsync(harness.Home.ResubmitAsync);
        Assert.Single(harness.Commands);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")]
    public async Task FailedHistoricalVersion_RetryUsesExactSource(string source)
    {
        var id = Guid.Parse(source);
        var response = WithVersions(Board(), Version(id, PanelGenerationStatus.Failed));
        await using var harness = await Harness.OpenAsync(response);
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, true, id));
        var body = Assert.Single(harness.Commands).Json<CreatePanelAttemptRequest>();
        Assert.Equal(id, body.SourceVersionId);
        Assert.True(body.Retry);
        Assert.Null(body.Prompt);
    }

    [Fact]
    public async Task SafetyFailure_RequiresDifferentPromptAndNeverOffersIdenticalRetry()
    {
        var response = WithVersions(Board(failed: true), Version(FailedVersion, PanelGenerationStatus.Failed,
            PanelGenerationIssue.ContentSafetyRejected));
        await using var harness = await Harness.OpenAsync(response);
        var html = await harness.HtmlAsync();
        Assert.Contains("consigne r", html);
        Assert.Contains("identique", html);
        Assert.DoesNotContain("Réessayer cette version", html);
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, true, FailedVersion));
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, true));
        await harness.RunAsync(() => harness.Home.Revise(1, "Original 1"));
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, false));
        Assert.Empty(harness.Commands);
        await harness.RunAsync(() => harness.Home.Revise(1, "Une scène révisée et paisible"));
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, false));
        Assert.False(Assert.Single(harness.Commands).Json<CreatePanelAttemptRequest>().Retry);
    }

    [Fact]
    public async Task SafetyHistory_HidesOlderTechnicalRetry_AndBlocksRejectedEffectivePrompt()
    {
        var source = Version(FailedVersion, PanelGenerationStatus.Failed) with
        {
            Prompt = "Earlier accepted scene", EffectivePrompt = "Earlier prepared scene"
        };
        var rejected = source with { Id = Alternative.ToString("D"), Issue = PanelGenerationIssue.ContentSafetyRejected };
        var response = WithVersions(Board(failed: true), source, rejected);
        await using var harness = await Harness.OpenAsync(response);

        Assert.DoesNotContain("Réessayer cette version", await harness.HtmlAsync());
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, true, FailedVersion));
        await harness.RunAsync(() => harness.Home.Revise(1, " Earlier prepared scene "));
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, false));
        Assert.Empty(harness.Commands);

        await harness.RunAsync(() => harness.Home.Revise(1, "A genuinely revised scene"));
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, false));
        Assert.False(Assert.Single(harness.Commands).Json<CreatePanelAttemptRequest>().Retry);
    }

    [Fact]
    public async Task NewAttempt_PreservesSelectedArtSceneAndUnsavedDraft_AndBlocksDoubleClick()
    {
        var response = Board();
        await using var harness = await Harness.OpenAsync(response);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.CommandHandler = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Json(WithVersions(response with { Revision = 13, Status = GenerationStatus.Pending },
                Version(Alternative, PanelGenerationStatus.Pending)));
        };
        await harness.RunAsync(() =>
        {
            harness.Home.Select(1);
            harness.Home.EditDraft(1, "Mon brouillon non sauvegardé");
            harness.Home.Revise(2, "Nouvelle consigne");
        });
        var task = harness.RunAsync(() => harness.Home.AttemptAsync(2, false));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await harness.RunAsync(() => harness.Home.AttemptAsync(2, false));
            await harness.RunAsync(() => harness.Home.AttemptAsync(1, true));
            await harness.RunAsync(() => harness.Home.SelectVersionAsync(1, Alternative));
            var sending = await harness.HtmlAsync();
            Assert.Contains("Envoi de la commande", sending);
            Assert.Contains("data:image/png;base64,AQ==", sending);
            Assert.Single(harness.Commands);
        }
        finally { release.TrySetResult(); await task; }
        Assert.Equal(1, harness.Home.SelectedPanel);
        Assert.Equal("Mon brouillon non sauvegardé", harness.Home.DraftPrompt(1));
        Assert.Equal("Nouvelle consigne", harness.Home.Revised(2));
        Assert.True(harness.Home.Dirty);
        Assert.True(harness.Home.IsGenerating);
        var body = Assert.Single(harness.Commands).Json<CreatePanelAttemptRequest>();
        Assert.Equal("Nouvelle consigne", body.Prompt);
        Assert.Null(body.SourceVersionId);
        Assert.False(body.Retry);
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, false));
        Assert.Single(harness.Commands);
    }

    [Fact]
    public async Task UncertainNetworkOrQueueFailure_RetainsUuidAndRevision_ButNotForDifferentPayload()
    {
        await using var harness = await Harness.OpenAsync(Board());
        var calls = 0;
        harness.CommandHandler = (_, _) =>
        {
            if (++calls == 1) throw new HttpRequestException("private network error");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        };
        await harness.RunAsync(() => harness.Home.Revise(1, "Version révisée"));
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, false));
        Assert.Contains("Envoi non confirm", await harness.HtmlAsync());
        harness.Response = harness.Response with { Revision = 14 };
        await harness.RunAsync(harness.Home.RefreshAsync);
        Assert.Contains("Envoi non confirm", await harness.HtmlAsync());
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, false));
        await harness.RunAsync(() => harness.Home.Revise(1, "Autre version révisée"));
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, false));
        var commands = harness.Commands.Select(r => r.Json<CreatePanelAttemptRequest>()).ToArray();
        Assert.Equal(3, commands.Length);
        Assert.Equal(commands[0], commands[1]);
        Assert.Equal(12, commands[1].ExpectedRevision);
        Assert.NotEqual(commands[0].AttemptId, commands[2].AttemptId);
        Assert.Equal(14, commands[2].ExpectedRevision);
        Assert.DoesNotContain("private network error", await harness.HtmlAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainSubmission_CanReplayExactCommandWhileBoardIsPending(bool selection)
    {
        var response = WithVersions(Board(), Version(Alternative, PanelGenerationStatus.Completed, image: "api/version-image"));
        await using var harness = await Harness.OpenAsync(response);
        harness.CommandHandler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await harness.RunAsync(() => harness.Home.Revise(1, "Première consigne"));
        await harness.RunAsync(() => selection
            ? harness.Home.SelectVersionAsync(1, Alternative) : harness.Home.AttemptAsync(1, false));
        harness.Response = response with { Status = GenerationStatus.Pending, Revision = 13 };
        await harness.RunAsync(harness.Home.RefreshAsync);
        await harness.RunAsync(() => harness.Home.Revise(1, "Une modification ultérieure"));
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, false));
        Assert.Single(harness.Commands);
        Assert.Contains("même commande", await harness.HtmlAsync());
        harness.CommandHandler = (_, _) => Task.FromResult(Json(harness.Response));
        await harness.RunAsync(harness.Home.ResubmitAsync);
        Assert.Equal(2, harness.Commands.Count);
        Assert.Equal(harness.Commands[0], harness.Commands[1]);
        Assert.DoesNotContain("Envoi non confirm", await harness.HtmlAsync());
        Assert.Equal("Une modification ultérieure", harness.Home.Revised(1));
    }

    [Fact]
    public async Task DefinitiveValidationFailure_ShowsSafeError_AndNextDecisionUsesNewUuid()
    {
        await using var harness = await Harness.OpenAsync(Board());
        harness.CommandHandler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("private provider details")
        });
        await harness.RunAsync(() => harness.Home.Revise(1, "Une consigne différente"));
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, false));
        var html = await harness.HtmlAsync();
        Assert.Contains("commande a", html);
        Assert.DoesNotContain("private provider details", html);
        Assert.DoesNotContain("même commande", html);
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, false));
        Assert.NotEqual(harness.Commands[0].Json<CreatePanelAttemptRequest>().AttemptId,
            harness.Commands[1].Json<CreatePanelAttemptRequest>().AttemptId);
    }

    [Fact]
    public async Task SafetyRejectedOriginalWithoutVersions_CannotRetry()
    {
        var original = Board(failed: true);
        var response = original with
        {
            Panels = original.Panels.Select(p => p.Number == 1
                ? p with { Issue = PanelGenerationIssue.ContentSafetyRejected } : p).ToArray()
        };
        await using var harness = await Harness.OpenAsync(response);
        Assert.DoesNotContain("Réessayer la case", await harness.HtmlAsync());
        await harness.RunAsync(() => harness.Home.AttemptAsync(1, true));
        Assert.Empty(harness.Commands);
    }

    [Fact]
    public async Task Selection_UsesExplicitPut_ThenRefreshReloadsVersionedImagesAndRevisionedBoard()
    {
        var original = Board();
        var response = WithVersions(original, Version(Alternative, PanelGenerationStatus.Completed,
            image: $"api/generations/{original.GenerationId}/panels/1/versions/{Alternative}/image"));
        await using var harness = await Harness.OpenAsync(response);
        harness.CommandHandler = (_, _) => Task.FromResult(Json(response with
        {
            Revision = 13, Status = GenerationStatus.Pending,
            Panels = response.Panels.Select(p => p.Number == 1 ? p with { SelectedVersionId = Alternative.ToString() } : p).ToArray()
        }));
        await harness.RunAsync(() => harness.Home.EditDraft(0, "Texte non enregistré"));
        await harness.RunAsync(() => harness.Home.SelectVersionAsync(1, Alternative));
        var selection = Assert.Single(harness.Commands);
        Assert.Equal(HttpMethod.Put, selection.Method);
        Assert.EndsWith("/panels/1/selection", selection.Path);
        var body = selection.Json<SelectPanelVersionRequest>();
        Assert.Equal(Alternative, body.VersionId);
        Assert.Equal(12, body.ExpectedRevision);
        Assert.NotEqual(Guid.Empty, body.CommandId);
        Assert.Contains("data:image/png;base64,AQ==", await harness.HtmlAsync());

        harness.Response = response with
        {
            Revision = 14,
            BoardUri = new("api/board?revision=14", UriKind.Relative),
            Panels = response.Panels.Select(p => p.Number == 1 ? p with
            {
                SelectedVersionId = Alternative.ToString(),
                ImageUri = new($"api/generations/{response.GenerationId}/panels/1/versions/{Alternative}/image", UriKind.Relative)
            } : p).ToArray()
        };
        harness.ImageHandler = (_, _) => Task.FromResult(Image(2));
        await harness.RunAsync(harness.Home.RefreshAsync);
        var refreshed = await harness.HtmlAsync();
        Assert.Contains("data:image/png;base64,Ag==", refreshed);
        Assert.Contains("revision=14", string.Join(" ", harness.Reads.Select(r => r.Path)));
        Assert.Contains($"/versions/{Alternative}/image", string.Join(" ", harness.Reads.Select(r => r.Path)));
        Assert.Equal("Texte non enregistré", harness.Home.DraftPrompt(0));
        Assert.Equal(0, harness.Home.SelectedPanel);
        Assert.True(harness.Home.Dirty);
        Assert.False(harness.Home.IsGenerating);
    }

    [Fact]
    public async Task ChangedArtifactUri_FailedDownloadKeepsPriorImage_ThenRetries()
    {
        await using var harness = await Harness.OpenAsync(Board());
        harness.Response = harness.Response with
        {
            Revision = 13,
            BoardUri = new("api/board?revision=13", UriKind.Relative),
            Panels = harness.Response.Panels.Select(p => p with
            {
                ImageUri = new($"api/panel/{p.Number}?version=2", UriKind.Relative)
            }).ToArray()
        };
        harness.ImageHandler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await harness.RunAsync(harness.Home.RefreshAsync);
        Assert.Contains("data:image/png;base64,AQ==", await harness.HtmlAsync());
        Assert.True(harness.Home.PendingArtifacts);
        Assert.Contains("actualisation", await harness.HtmlAsync());
        harness.ImageHandler = (_, _) => Task.FromResult(Image(3));
        await harness.RunAsync(harness.Home.RefreshAsync);
        Assert.Contains("data:image/png;base64,Aw==", await harness.HtmlAsync());
        Assert.False(harness.Home.PendingArtifacts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleRevision_RefreshesAndSurfacesConflict_WithoutLosingDraft(bool selection)
    {
        var response = WithVersions(Board(), Version(Alternative, PanelGenerationStatus.Completed, image: "api/version-image"));
        await using var harness = await Harness.OpenAsync(response);
        harness.CommandHandler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("private server detail")
        });
        await harness.RunAsync(() =>
        {
            harness.Home.Select(1);
            harness.Home.EditDraft(1, "Texte conservé");
            harness.Home.Revise(1, "Consigne conservée");
        });
        harness.Response = response with { Revision = 21 };
        await harness.RunAsync(() => selection
            ? harness.Home.SelectVersionAsync(1, Alternative) : harness.Home.AttemptAsync(1, false));
        var html = await harness.HtmlAsync();
        Assert.Contains("409", html);
        Assert.DoesNotContain("private server detail", html);
        Assert.Equal(1, harness.Home.SelectedPanel);
        Assert.Equal("Texte conservé", harness.Home.DraftPrompt(1));
        Assert.Equal("Consigne conservée", harness.Home.Revised(1));
        Assert.True(harness.Home.Dirty);
        Assert.Equal(21, harness.Home.Revision);
        await harness.RunAsync(() => selection
            ? harness.Home.SelectVersionAsync(1, Alternative) : harness.Home.AttemptAsync(1, false));
        Assert.Equal(2, harness.Commands.Count);
        if (selection)
        {
            var first = harness.Commands[0].Json<SelectPanelVersionRequest>();
            var second = harness.Commands[1].Json<SelectPanelVersionRequest>();
            Assert.NotEqual(first.CommandId, second.CommandId);
            Assert.Equal(21, second.ExpectedRevision);
        }
        else
        {
            var first = harness.Commands[0].Json<CreatePanelAttemptRequest>();
            var second = harness.Commands[1].Json<CreatePanelAttemptRequest>();
            Assert.NotEqual(first.AttemptId, second.AttemptId);
            Assert.Equal(21, second.ExpectedRevision);
        }
    }

    [Fact]
    public async Task TerminalFailedAlternative_RemainsVisibleWithoutReplacingSelectedSuccess()
    {
        var response = WithVersions(Board(), Version(FailedVersion, PanelGenerationStatus.Failed,
            PanelGenerationIssue.ProviderThrottled));
        await using var harness = await Harness.OpenAsync(response);
        var html = await harness.HtmlAsync();
        Assert.Contains("data:image/png;base64,AQ==", html);
        Assert.Contains("tentative est termin", html);
        Assert.Contains("Réessayer cette version", html);
        Assert.Contains("Version alternative", html);
        Assert.False(harness.Home.IsGenerating);
        await harness.RunAsync(() => harness.Home.SelectVersionAsync(1, FailedVersion));
        Assert.Empty(harness.Commands);
    }

    [Fact]
    public async Task PendingAlternative_BlocksCommandsAcrossBoardEvenWhenSelectedPanelIsCompleted()
    {
        var response = WithVersions(Board(), Version(Alternative, PanelGenerationStatus.Processing));
        await using var harness = await Harness.OpenAsync(response);
        Assert.True(harness.Home.IsGenerating);
        await harness.RunAsync(() => harness.Home.Revise(2, "Une autre scène"));
        await harness.RunAsync(() => harness.Home.AttemptAsync(2, false));
        Assert.Empty(harness.Commands);
        Assert.Contains("data:image/png;base64,AQ==", await harness.HtmlAsync());
    }

    [Fact]
    public async Task ReopenedPendingCommand_CanResumeWithoutCreatingAnotherVersion_AndRetryDispatchFailure()
    {
        var id = Guid.NewGuid();
        var response = WithVersions(Board() with
        {
            Status = GenerationStatus.Pending, PendingCommandId = id.ToString("D")
        }, Version(id, PanelGenerationStatus.Pending));
        await using var harness = await Harness.OpenAsync(response);
        Assert.Contains("Reprendre la commande accept", await harness.HtmlAsync());
        harness.CommandHandler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await harness.RunAsync(harness.Home.ResumePendingAsync);
        Assert.Contains("Envoi non confirm", await harness.HtmlAsync());
        harness.CommandHandler = (_, _) => Task.FromResult(Json(response));
        await harness.RunAsync(harness.Home.ResubmitAsync);
        Assert.Equal(2, harness.Commands.Count);
        Assert.All(harness.Commands, command =>
        {
            Assert.Equal(HttpMethod.Post, command.Method);
            Assert.Equal($"/api/generations/{response.GenerationId:D}/commands/{id:D}/resume", command.Path);
            Assert.Empty(command.Body);
        });
        Assert.DoesNotContain("Envoi non confirm", await harness.HtmlAsync());
        Assert.True(harness.Home.IsGenerating);
    }

    [Fact]
    public async Task FailedComposition_CanReselectCurrentVersion_WithoutChangingSceneOrDraft()
    {
        var response = WithVersions(Board() with { Status = GenerationStatus.Failed },
            Version(Guid.Empty, PanelGenerationStatus.Completed, image: "api/original-image"));
        await using var harness = await Harness.OpenAsync(response);
        Assert.Contains("Réessayer la composition", WebUtility.HtmlDecode(await harness.HtmlAsync()));
        await harness.RunAsync(() => harness.Home.EditDraft(0, "Unsaved scene"));
        await harness.RunAsync(() => harness.Home.SelectVersionAsync(1, Guid.Empty));
        var command = Assert.Single(harness.Commands).Json<SelectPanelVersionRequest>();
        Assert.Equal(Guid.Empty, command.VersionId);
        Assert.Equal(response.Revision, command.ExpectedRevision);
        Assert.Equal(0, harness.Home.SelectedPanel);
        Assert.Equal("Unsaved scene", harness.Home.DraftPrompt(0));
        Assert.True(harness.Home.Dirty);
    }

    [Fact]
    public async Task Navigation_CancelsPendingCommandAndIgnoresLateResponse()
    {
        var first = Board();
        await using var harness = await Harness.OpenAsync(first);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        harness.CommandHandler = async (_, token) =>
        {
            using var registration = token.Register(() => cancelled = true);
            entered.TrySetResult();
            await release.Task;
            return Json(first with { Revision = 99, Status = GenerationStatus.Pending });
        };
        await harness.RunAsync(() => harness.Home.Revise(1, "Autre consigne"));
        var command = harness.RunAsync(() => harness.Home.AttemptAsync(1, false));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            harness.Response = Board() with { Title = "Deuxième planche", Revision = 30 };
            await harness.RunAsync(() => harness.Home.OpenBoardAsync(harness.Response.GenerationId));
        }
        finally { release.TrySetResult(); await command; }
        await harness.RunAsync(() => harness.Home.Select(0));
        Assert.True(cancelled);
        Assert.Equal(30, harness.Home.Revision);
        Assert.False(harness.Home.IsGenerating);
        Assert.Contains("Deuxi", await harness.HtmlAsync());
        Assert.DoesNotContain("Envoi non confirm", await harness.HtmlAsync());
    }

    [Fact]
    public async Task OlderRefresh_CannotOverwriteNewerRevision()
    {
        await using var harness = await Harness.OpenAsync(Board());
        harness.Response = harness.Response with { Revision = 2, Status = GenerationStatus.Pending };
        await harness.RunAsync(harness.Home.RefreshAsync);
        Assert.Equal(12, harness.Home.Revision);
        Assert.False(harness.Home.IsGenerating);
    }

    [Fact]
    public async Task VersionPreview_UsesPrivateVersionUri_ReportsFailureAndOffersDownloadAfterRetry()
    {
        var id = Guid.NewGuid();
        var version = Version(Alternative, PanelGenerationStatus.Completed,
            image: $"api/generations/{id}/panels/1/versions/{Alternative}/image");
        var response = WithVersions(Board() with { GenerationId = id }, version);
        await using var harness = await Harness.OpenAsync(response);
        TestVersionEditor? editor = null;
        var root = await harness.Renderer.Dispatcher.InvokeAsync(() => harness.Renderer.RenderComponentAsync<TestVersionEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(TestVersionEditor.Panel)] = response.Panels[0],
                [nameof(TestVersionEditor.CanEdit)] = true,
                [nameof(TestVersionEditor.Prepare)] = (Action<TestVersionEditor>)(component => editor = component)
            })));
        harness.ImageHandler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        await harness.RunAsync(() => editor!.PreviewAsync(version));
        var failed = await harness.Renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("role=\"alert\"", failed);
        Assert.DoesNotContain("download=", failed);
        harness.ImageHandler = (_, _) => Task.FromResult(Image(5));
        await harness.RunAsync(() => editor!.PreviewAsync(version));
        var html = await harness.Renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("data:image/png;base64,BQ==", html);
        Assert.Contains($"mai-comic-panel-1-version-{Alternative}.png", html);
        Assert.Contains("alt=\"Aper", html);
        Assert.DoesNotContain("role=\"alert\"", html);
        Assert.Equal(2, harness.Reads.Count(r => r.Path.EndsWith($"/versions/{Alternative}/image")));
        Assert.Empty(harness.Commands);
    }

    private static GenerationResponse Board(bool failed = false)
    {
        var id = Guid.NewGuid();
        return new(id, "Planche de test", null, "Contexte figé", Created, false, BoardTemplate.Standard,
            [], GenerationStatus.Completed, 6, failed ? 5 : 6,
            Enumerable.Range(1, 6).Select(number => new PanelGenerationResponse(number, $"Original {number}", [],
                failed && number == 1 ? PanelGenerationStatus.Failed : PanelGenerationStatus.Completed,
                null, failed && number == 1 ? null : new($"api/generations/{id}/panels/{number}?version=original", UriKind.Relative),
                null, Issue: failed && number == 1 ? PanelGenerationIssue.TechnicalFailure : null)).ToArray(),
            new("api/board?revision=12", UriKind.Relative), null, Revision: 12, CanEditPanels: true);
    }

    private static PanelVersionResponse Version(Guid id, PanelGenerationStatus status,
        PanelGenerationIssue? issue = null, string? image = null) =>
        new(id.ToString(), "Original 1", "Consigne effective", status,
            image is null ? null : new(image, UriKind.Relative), issue, [], Created);

    private static GenerationResponse WithVersions(GenerationResponse response, params PanelVersionResponse[] versions) =>
        response with
        {
            Panels = response.Panels.Select(p => p.Number == 1 ? p with
            {
                SelectedVersionId = Guid.Empty.ToString(), Versions = versions
            } : p).ToArray()
        };

    private static HttpResponseMessage Json(GenerationResponse response) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
    private static HttpResponseMessage Image(byte value) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([value]) { Headers = { ContentType = new("image/png") } }
    };

    private sealed record Request(HttpMethod Method, string Path, string Body)
    {
        public T Json<T>() => JsonSerializer.Deserialize<T>(Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly HttpClient client;
        private readonly ServiceProvider services;
        private HtmlRootComponent root;
        public GenerationResponse Response;
        public TestHome Home = default!;
        public HtmlRenderer Renderer { get; }
        public List<Request> Commands { get; } = [];
        public List<Request> Reads { get; } = [];
        public Func<Request, CancellationToken, Task<HttpResponseMessage>>? CommandHandler;
        public Func<Request, CancellationToken, Task<HttpResponseMessage>>? ImageHandler;

        private Harness(GenerationResponse response)
        {
            Response = response;
            client = new(new Handler(SendAsync)) { BaseAddress = new("https://example.test/") };
            services = new ServiceCollection().AddLogging()
                .AddSingleton(new GenerationApiClient(client)).AddSingleton<StoryDraftState>()
                .AddSingleton<IAccessTokenProvider, NoTokenProvider>()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
                .AddSingleton<NavigationManager, TestNavigation>()
                .AddSingleton<NotificationService>().AddSingleton<DialogService>()
                .AddSingleton<IJSRuntime, NoJsRuntime>().BuildServiceProvider();
            Renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        }

        public static async Task<Harness> OpenAsync(GenerationResponse response)
        {
            var harness = new Harness(response);
            harness.root = await harness.Renderer.Dispatcher.InvokeAsync(() => harness.Renderer.RenderComponentAsync<TestHome>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(TestHome.Prepare)] = (Action<TestHome>)(home => harness.Home = home)
                })));
            await harness.RunAsync(() => harness.Home.OpenBoardAsync(response.GenerationId));
            await harness.RunAsync(() => harness.Home.Select(0));
            return harness;
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            var request = new Request(message.Method, message.RequestUri!.PathAndQuery,
                message.Content is null ? "" : await message.Content.ReadAsStringAsync(token));
            if (message.Method != HttpMethod.Get)
            {
                Commands.Add(request);
                return CommandHandler is not null ? await CommandHandler(request, token) : Json(Response);
            }
            Reads.Add(request);
            if (message.RequestUri.AbsolutePath == $"/api/generations/{Response.GenerationId:D}") return Json(Response);
            return ImageHandler is not null ? await ImageHandler(request, token) : Image(1);
        }

        public Task RunAsync(Action action) => Renderer.Dispatcher.InvokeAsync(action);
        public Task RunAsync(Func<Task> action) => Renderer.Dispatcher.InvokeAsync(action);
        public Task<string> HtmlAsync() => Renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        public async ValueTask DisposeAsync()
        {
            await Renderer.DisposeAsync();
            services.Dispose();
            client.Dispose();
        }
    }

    public sealed class TestHome : Home
    {
        [Parameter] public Action<TestHome> Prepare { get; set; } = default!;
        protected override Task OnInitializedAsync() { Prepare(this); return Task.CompletedTask; }
        protected override Task OnParametersSetAsync() => Task.CompletedTask;
        public async Task OpenBoardAsync(Guid id) { GenerationId = id; await base.OnParametersSetAsync(); StateHasChanged(); }
        public Task RefreshAsync() => Call("RefreshGenerationAsync");
        public Task AttemptAsync(int number, bool retry, Guid? source = null) => Call("CreatePanelAttemptAsync", number, retry, source);
        public Task SelectVersionAsync(int number, Guid version) => Call("SelectPanelVersionAsync", number, version);
        public Task ResubmitAsync() => Call("ResubmitUncertainPanelCommandAsync");
        public Task ResumePendingAsync() => Call("ResumePendingPanelCommandAsync");
        public void Revise(int number, string value) { Method("SetRevisedPrompt").Invoke(this, [number, value]); StateHasChanged(); }
        public string Revised(int number) => (string)Method("RevisedPrompt").Invoke(this, [number])!;
        public void Select(int index) { Method("SelectPanel").Invoke(this, [index]); StateHasChanged(); }
        public void Move(int offset) { Method("MovePanel").Invoke(this, [offset]); StateHasChanged(); }
        public void ReplaceScaffold()
        {
            Method("ApplyScaffold").Invoke(this,
                [new ProjectScaffoldResponse("Nouveau storyboard", "", "", Enumerable.Repeat("Nouvelle scène", 6).ToArray()), false]);
            StateHasChanged();
        }
        public void EditDraft(int index, string prompt) { Drafts[index].Prompt = prompt; StateHasChanged(); }
        public string DraftPrompt(int index) => Drafts[index].Prompt;
        public int SelectedPanel => (int)Field("selectedPanel").GetValue(this)!;
        public long Revision => ((GenerationResponse)Field("generationResult").GetValue(this)!).Revision;
        public bool IsGenerating => (bool)Field("isGenerating").GetValue(this)!;
        public bool Dirty => Property("HasUnsavedChanges");
        public bool PendingArtifacts => Property("HasPendingArtifacts");
        private List<ComicPanelDraft> Drafts => (List<ComicPanelDraft>)Field("panels").GetValue(this)!;
        private bool Property(string name) => (bool)typeof(Home).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
        private Task Call(string name, params object?[] args) => (Task)Method(name).Invoke(this, args)!;
        private static FieldInfo Field(string name) => typeof(Home).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static MethodInfo Method(string name) => typeof(Home).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    }

    public sealed class TestVersionEditor : PanelVersionEditor
    {
        [Parameter] public Action<TestVersionEditor> Prepare { get; set; } = default!;
        protected override void OnInitialized() => Prepare(this);
        public async Task PreviewAsync(PanelVersionResponse version)
        {
            await (Task)typeof(PanelVersionEditor).GetMethod("LoadPreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(this, [version])!;
            StateHasChanged();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://example.test/", "https://example.test/boards/new");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }
    private sealed class NoTokenProvider : IAccessTokenProvider
    {
        public ValueTask<AccessTokenResult> RequestAccessToken() => throw new NotSupportedException();
        public ValueTask<AccessTokenResult> RequestAccessToken(AccessTokenRequestOptions options) => throw new NotSupportedException();
    }
    private sealed class NoJsRuntime : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
