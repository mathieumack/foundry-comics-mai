using System.Net;
using System.Text.Json;
using MaiImageGenerator.Web.Components;
using MaiImageGenerator.Web.Models;
using MaiImageGenerator.Web.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace MaiImageGenerator.Web.Pages;

public partial class Home
{
    private readonly Dictionary<int, string> revisedPanelPrompts = [];
    private readonly Dictionary<PanelCommandKey, PanelCommand> panelCommands = [];
    private bool isPanelCommandSubmitting;
    private string? panelCommandError;
    private Uri? loadedBoardUri;
    private PanelCommandKey? uncertainPanelCommand;
    private bool UncertainCommandRetryLocked => isPanelCommandSubmitting || isRefreshing || isBoardLoading ||
        isSubmitting || isScaffolding || isCharacterSaving;

    private bool PanelCommandsLocked => EditingLocked || isRefreshing || isPanelCommandSubmitting ||
        generationResult?.Panels.Any(p => p.Versions?.Any(v =>
            v.Status is PanelGenerationStatus.Pending or PanelGenerationStatus.Processing) == true) == true;

    private string RevisedPrompt(int number) => revisedPanelPrompts.GetValueOrDefault(number, string.Empty);
    private void SetRevisedPrompt(int number, string value) => revisedPanelPrompts[number] = value;

    private void ResetPanelVersionState()
    {
        revisedPanelPrompts.Clear();
        panelCommands.Clear();
        uncertainPanelCommand = null;
        panelCommandError = null;
        isPanelCommandSubmitting = false;
        loadedBoardUri = null;
    }

    private Task CreatePanelAttemptAsync(int number, bool retry, Guid? sourceVersionId = null)
    {
        var panel = generationResult?.Panels.SingleOrDefault(p => p.Number == number);
        if (panel is null || generationResult?.CanEditPanels != true || PanelCommandsLocked) return Task.CompletedTask;
        var prompt = retry ? null : RevisedPrompt(number).Trim();
        if (retry)
        {
            var source = panel.Versions?.SingleOrDefault(v =>
                Guid.TryParse(v.Id, out var versionId) && versionId == sourceVersionId);
            if (source is null
                ? sourceVersionId is not null || panel.Versions is { Count: > 0 } ||
                  panel.Status != PanelGenerationStatus.Failed || panel.Issue == PanelGenerationIssue.ContentSafetyRejected
                : !PanelVersionEditor.CanRetry(panel, source))
                return Task.CompletedTask;
        }
        else if (!PanelVersionEditor.IsRevisedPrompt(panel, prompt))
        {
            panelCommandError = "Décrivez une consigne révisée, différente de la consigne initiale ou refusée.";
            return Task.CompletedTask;
        }
        return SendPanelCommandAsync(new(number, retry, prompt, sourceVersionId, null));
    }

    private Task SelectPanelVersionAsync(int number, Guid versionId)
    {
        var panel = generationResult?.Panels.SingleOrDefault(p => p.Number == number);
        if (generationResult?.CanEditPanels != true || PanelCommandsLocked ||
            panel?.Versions?.Any(v => Guid.TryParse(v.Id, out var id) && id == versionId &&
                v.Status == PanelGenerationStatus.Completed && v.ImageUri is not null) != true ||
            (generationResult.Status != GenerationStatus.Failed &&
             (Guid.TryParse(panel.SelectedVersionId, out var selected) && selected == versionId ||
              panel.SelectedVersionId is null && versionId == Guid.Empty)))
            return Task.CompletedTask;
        return SendPanelCommandAsync(new(number, false, null, null, versionId));
    }

    private Task ResubmitUncertainPanelCommandAsync() =>
        uncertainPanelCommand is { } key && panelCommands.ContainsKey(key) && !UncertainCommandRetryLocked
            ? SendPanelCommandAsync(key, resubmitUncertain: true) : Task.CompletedTask;

    private Task ResumePendingPanelCommandAsync() =>
        Guid.TryParse(generationResult?.PendingCommandId, out var id) && id != Guid.Empty
            ? SendPanelCommandAsync(new(0, false, null, null, null, id)) : Task.CompletedTask;

    private async Task SendPanelCommandAsync(PanelCommandKey key, bool resubmitUncertain = false)
    {
        if (activeGenerationId is not Guid boardId || generationResult?.CanEditPanels != true ||
            (resubmitUncertain || key.Resume is not null ? UncertainCommandRetryLocked : PanelCommandsLocked) ||
            (resubmitUncertain && (uncertainPanelCommand != key || !panelCommands.ContainsKey(key)))) return;
        var token = boardCancellation?.Token ?? lifetime.Token;
        // Keep both the UUID and the original revision after an uncertain submission.
        // A changed payload gets its own identity, even after an intervening status refresh.
        if (!panelCommands.TryGetValue(key, out var command))
            panelCommands[key] = command = new(Guid.NewGuid(), generationResult.Revision);
        isPanelCommandSubmitting = true;
        panelCommandError = null;
        StateHasChanged();
        try
        {
            var result = key.Resume is Guid resumeId
                ? await GenerationClient.ResumePanelCommandAsync(boardId, resumeId, token)
                : key.Selection is Guid versionId
                ? await GenerationClient.SelectPanelVersionAsync(boardId, key.Number,
                    new(command.Id, versionId, command.Revision), token)
                : await GenerationClient.CreatePanelAttemptAsync(boardId, key.Number,
                    new(command.Id, command.Revision, key.Retry, key.Prompt, key.Source), token);
            token.ThrowIfCancellationRequested();
            if (activeGenerationId != boardId) return;
            panelCommands.Remove(key);
            if (uncertainPanelCommand == key) uncertainPanelCommand = null;
            await ApplyGenerationAsync(result, token);
        }
        catch (AccessTokenNotAvailableException exception) { exception.Redirect(); }
        catch (GenerationApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            if (token.IsCancellationRequested || activeGenerationId != boardId) return;
            panelCommands.Remove(key);
            if (uncertainPanelCommand == key) uncertainPanelCommand = null;
            // Release the HTTP guard so the authoritative refresh can execute.
            isPanelCommandSubmitting = false;
            await RefreshGenerationAsync();
            if (!token.IsCancellationRequested && activeGenerationId == boardId)
                panelCommandError = "La planche a changé (conflit 409). Une actualisation a été demandée ; vérifiez les versions avant de réessayer.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is GenerationApiException or HttpRequestException or OperationCanceledException or JsonException)
        {
            if (token.IsCancellationRequested || activeGenerationId != boardId) return;
            if (exception is GenerationApiException { StatusCode: { } status } &&
                (int)status is >= 400 and < 500 && status is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests))
            {
                panelCommands.Remove(key);
                if (uncertainPanelCommand == key) uncertainPanelCommand = null;
                panelCommandError = "La commande a été refusée. Vérifiez la consigne et actualisez la planche avant de réessayer.";
            }
            else
            {
                uncertainPanelCommand = key;
                panelCommandError = "Envoi non confirmé (réseau ou file indisponible). Actualisez la planche. Réessayer la même commande conservera son identifiant pour éviter un doublon.";
            }
        }
        finally
        {
            if (!token.IsCancellationRequested && activeGenerationId == boardId)
            {
                isPanelCommandSubmitting = false;
                StateHasChanged();
                if (refreshRequested) await RefreshGenerationAsync();
            }
        }
    }

    private sealed record PanelCommandKey(int Number, bool Retry, string? Prompt, Guid? Source, Guid? Selection, Guid? Resume = null);
    private sealed record PanelCommand(Guid Id, long Revision);
}
