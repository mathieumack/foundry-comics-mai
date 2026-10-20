using System.Text.Json;
using MaiImageGenerator.Web.Models;
using MaiImageGenerator.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using Radzen;

namespace MaiImageGenerator.Web.Pages;

public partial class Home : IAsyncDisposable
{
    [Inject] private GenerationApiClient GenerationClient { get; set; } = default!;
    [Inject] private StoryDraftState Drafts { get; set; } = default!;
    [Inject] private IAccessTokenProvider TokenProvider { get; set; } = default!;
    [Inject] private IConfiguration Configuration { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private NotificationService NotificationService { get; set; } = default!;
    [Inject] private DialogService Dialogs { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Parameter] public Guid? ProjectId { get; set; }
    [Parameter] public Guid? BoardId { get; set; }
    [SupplyParameterFromQuery(Name = "generationId")] public Guid? GenerationId { get; set; }

    private readonly List<ComicPanelDraft> panels = Enumerable.Range(0, 6).Select(_ => new ComicPanelDraft()).ToList();
    private readonly List<CharacterDraft> characters = [];
    private readonly List<GenerationSummaryResponse> projectBoards = [];
    private readonly CancellationTokenSource lifetime = new();
    private HubConnection? notificationConnection;
    private CancellationTokenSource? boardCancellation;
    private Guid? activeGenerationId, loadedProjectId, loadedGenerationId;
    private ElementReference imageViewerDialog;
    private ProjectResponse? project;
    private string boardTitle = "Nouvelle planche", newCharacterName = string.Empty;
    private string? storyDescription, projectDescription, context, boardDataUrl, progressMessage, projectError, workspaceError, viewerImageUrl, viewerImageAlt;
    private string baseline = string.Empty, characterBaseline = string.Empty;
    private int generatedPanelCount, selectedPanel;
    private BoardTemplate selectedTemplate;
    private StudioTab activeTab;
    private bool initialized, rewritePrompt = true, isGenerating, isSubmitting, isRefreshing, isScaffolding, isProjectLoading, isBoardLoading = true, isCharacterSaving;
    private bool refreshRequested, allowNavigation, viewerOpening;
    private bool EditingLocked => isGenerating || isSubmitting || isScaffolding || isCharacterSaving || isBoardLoading;
    private bool HasUnsavedChanges => !allowNavigation && initialized && (isScaffolding || isCharacterSaving || !string.IsNullOrWhiteSpace(newCharacterName) || CaptureDraft() != baseline || CaptureCharacters() != characterBaseline);
    private BoardTemplateDefinition DisplayTemplateDefinition => BoardTemplateDefinitions.Resolve(selectedTemplate, panels.Count);
    private string BoardGridStyle => $"grid-template-columns:repeat({DisplayTemplateDefinition.Columns},minmax(0,1fr));";

    protected override async Task OnInitializedAsync()
    {
        notificationConnection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(Configuration["ApiBaseUrl"] ?? throw new InvalidOperationException("ApiBaseUrl is required.")), "hubs/notifications"), options =>
            {
                options.AccessTokenProvider = async () =>
                {
                    var result = await TokenProvider.RequestAccessToken(new AccessTokenRequestOptions
                    {
                        Scopes = [Configuration["AzureAd:DefaultAccessTokenScopes:0"] ?? throw new InvalidOperationException("The API scope is required.")]
                    });
                    return result.TryGetToken(out var token) ? token.Value : throw new AccessTokenNotAvailableException(Navigation, result, []);
                };
            }).WithAutomaticReconnect().Build();
        notificationConnection.On<PanelNotification>("PanelGenerationStarted", n => InvokeAsync(() =>
        {
            if (TryGetNotifiedPanel(n, out var panel))
            {
                panel.Status = PanelUiStatus.Generating;
                isGenerating = true;
                progressMessage = $"Illustration de la case {n.PanelNumber} sur {panels.Count}";
                StateHasChanged();
            }
        }));
        notificationConnection.On<PanelNotification>("PanelGenerationCompleted", n => InvokeAsync(() => HandlePanelCompletedAsync(n)));
        notificationConnection.Reconnected += _ => InvokeAsync(RefreshGenerationAsync);
        try { await notificationConnection.StartAsync(lifetime.Token); }
        catch (AccessTokenNotAvailableException exception) { exception.Redirect(); }
        catch (Exception exception) when (exception is HttpRequestException or HubException)
        {
            Notify(NotificationSeverity.Warning, "Suivi en direct indisponible", $"{exception.Message} Utilisez Actualiser la planche.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    protected override async Task OnParametersSetAsync()
    {
        var requestedId = BoardId ?? GenerationId;
        if (initialized && loadedProjectId == ProjectId && loadedGenerationId == requestedId) return;
        CloseImageViewer();
        boardCancellation?.Cancel();
        boardCancellation?.Dispose();
        boardCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = boardCancellation.Token;
        allowNavigation = false;
        projectError = workspaceError = null;
        isBoardLoading = true;
        try
        {
            project = null;
            projectBoards.Clear();
            if (ProjectId is Guid projectId)
            {
                isProjectLoading = true;
                project = await GenerationClient.GetProjectAsync(projectId, token);
                if (project.Type != ProjectType.Comic)
                {
                    Navigation.NavigateTo($"/projects/{projectId:D}/book", replace: true);
                    return;
                }
                projectBoards.AddRange(await GenerationClient.ListBoardsAsync(projectId, token));
            }
            if (requestedId is Guid generationId && ProjectId is not null && projectBoards.All(b => b.GenerationId != generationId))
            {
                projectError = "Cette planche n'appartient pas au projet sélectionné.";
                return;
            }
            ResetBoard();
            if (project is not null) ApplyProjectDefaults();
            baseline = CaptureDraft();
            characterBaseline = CaptureCharacters();
            if (requestedId is Guid id)
            {
                var generation = await GenerationClient.GetAsync(id, token);
                boardTitle = generation.Title;
                projectDescription = generation.Description;
                context = generation.Context;
                rewritePrompt = generation.RewritePrompt;
                selectedTemplate = generation.Template;
                activeGenerationId = id;
                if (project is null)
                {
                    characters.Clear();
                    characters.AddRange(generation.Characters.Select(c => ToDraft(c.Id, c.Name, c.References)));
                }
                panels.Clear();
                panels.AddRange(generation.Panels.OrderBy(p => p.Number).Select(p => new ComicPanelDraft
                {
                    Prompt = p.Prompt,
                    CharacterIds = p.CharacterIds.Where(id => characters.Any(c => c.Id == id)).ToArray()
                }));
                await ApplyGenerationAsync(generation, token);
                activeTab = StudioTab.Preview;
                baseline = CaptureDraft();
                characterBaseline = CaptureCharacters();
            }
            else if (ProjectId is Guid draftProjectId && Drafts.Take(draftProjectId) is { } prepared)
            {
                selectedTemplate = prepared.Template;
                ApplyScaffold(prepared.Scaffold, preserveTitle: true);
            }
            loadedProjectId = ProjectId;
            loadedGenerationId = requestedId;
            savedCharacterNames.Clear();
            foreach (var character in characters) savedCharacterNames[character.Id] = character.Name;
            initialized = true;
        }
        catch (AccessTokenNotAvailableException exception) { exception.Redirect(); }
        catch (Exception exception) when (exception is GenerationApiException or HttpRequestException) { projectError = exception.Message; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { isProjectLoading = isBoardLoading = false; }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!viewerOpening) return;
        viewerOpening = false;
        await JS.InvokeVoidAsync("maiStudio.showModal", imageViewerDialog);
    }

    private void ResetBoard()
    {
        activeGenerationId = null;
        boardTitle = "Nouvelle planche";
        projectDescription = context = storyDescription = boardDataUrl = progressMessage = viewerImageUrl = viewerImageAlt = null;
        viewerOpening = false;
        generatedPanelCount = selectedPanel = 0;
        selectedTemplate = BoardTemplate.Standard;
        rewritePrompt = true;
        isGenerating = isSubmitting = isRefreshing = false;
        activeTab = StudioTab.Scenes;
        characters.Clear();
        panels.Clear();
        panels.AddRange(Enumerable.Range(0, 6).Select(_ => new ComicPanelDraft()));
    }

    private void ApplyProjectDefaults()
    {
        context = project!.Context;
        storyDescription = project.SourceIdea;
        characters.AddRange(project.Characters.Select(c => ToDraft(c.CharacterId, c.Name, c.References)));
    }

    private static CharacterDraft ToDraft(string id, string name, IReadOnlyList<CharacterReferenceResponse> references)
    {
        var draft = new CharacterDraft { Id = id, Name = name };
        draft.References.AddRange(references.Select(r => new UploadedReference(r.ReferenceId, r.FileName)));
        return draft;
    }

    private async Task CreateScaffoldAsync()
    {
        if (string.IsNullOrWhiteSpace(storyDescription)) { workspaceError = "Décrivez votre idée avant de proposer les scènes."; return; }
        if (panels.Any(p => !string.IsNullOrWhiteSpace(p.Prompt)) && !await ConfirmAsync("Remplacer vos descriptions actuelles par de nouvelles propositions ?")) return;
        isScaffolding = true;
        workspaceError = null;
        try
        {
            var scaffold = await GenerationClient.CreateScaffoldAsync(new CreateProjectScaffoldRequest(storyDescription.Trim(), panels.Count), lifetime.Token);
            ApplyScaffold(scaffold);
            Notify(NotificationSeverity.Success, "Vos scènes sont prêtes", "Relisez et modifiez les propositions avant de créer les images.");
        }
        catch (AccessTokenNotAvailableException exception) { exception.Redirect(); }
        catch (Exception exception) when (exception is GenerationApiException or HttpRequestException) { workspaceError = exception.Message; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { isScaffolding = false; }
    }

    private void ApplyScaffold(ProjectScaffoldResponse scaffold, bool preserveTitle = false)
    {
        boardTitle = preserveTitle ? project!.Title : scaffold.Title;
        projectDescription = scaffold.Description;
        context = preserveTitle ? project!.Context : scaffold.Context;
        panels.Clear();
        panels.AddRange(scaffold.PanelPrompts.Select(prompt => new ComicPanelDraft { Prompt = prompt }));
        selectedPanel = 0;
        boardDataUrl = null;
        activeGenerationId = null;
    }

    private async Task GenerateBoardAsync()
    {
        workspaceError = null;
        var emptyPanel = panels.FindIndex(p => string.IsNullOrWhiteSpace(p.Prompt));
        if (string.IsNullOrWhiteSpace(boardTitle)) { workspaceError = "Donnez un titre à votre planche."; return; }
        if (emptyPanel >= 0) { selectedPanel = emptyPanel; activeTab = StudioTab.Scenes; workspaceError = $"Décrivez la case {emptyPanel + 1} avant de lancer les images."; return; }
        if (ProjectId is null && characters.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.References.Count == 0))
        {
            workspaceError = "Chaque personnage doit avoir un nom et au moins une référence. Ajoutez une image ou retirez le personnage.";
            activeTab = StudioTab.Characters;
            return;
        }
        if (ProjectId is not null && CaptureCharacters() != characterBaseline)
        {
            workspaceError = "Enregistrez les noms de vos personnages avant de générer la planche.";
            activeTab = StudioTab.Characters;
            return;
        }
        isSubmitting = true;
        try
        {
            var requests = panels.Select(p => new PanelRequest(p.Prompt.Trim(), p.CharacterIds.ToArray())).ToArray();
            GenerationResponse generation;
            if (ProjectId is Guid projectId && project is not null)
            {
                if (project.Context != context?.Trim())
                    project = await GenerationClient.UpdateProjectAsync(projectId, new CreateProjectRequest(project.Title, context?.Trim(), ProjectType.Comic, project.SourceIdea), lifetime.Token);
                generation = await GenerationClient.CreateBoardAsync(projectId,
                    new CreateBoardRequest(boardTitle.Trim(), projectDescription?.Trim(), rewritePrompt, selectedTemplate, requests), lifetime.Token);
                projectBoards.Insert(0, new(generation.GenerationId, generation.Title, generation.CreatedAt, generation.Status, null));
            }
            else
            {
                generation = await GenerationClient.CreateAsync(new CreateGenerationRequest(boardTitle.Trim(), projectDescription?.Trim(),
                    context?.Trim(), rewritePrompt, selectedTemplate, requests,
                    characters.Select(c => new CharacterRequest(c.Id, c.Name.Trim(), c.References.Select(r => r.ReferenceId).ToArray())).ToArray()), lifetime.Token);
            }
            activeGenerationId = generation.GenerationId;
            loadedGenerationId = activeGenerationId;
            boardDataUrl = null;
            foreach (var panel in panels) { panel.ImageDataUrl = panel.EffectivePrompt = panel.Error = null; panel.Status = PanelUiStatus.Pending; }
            baseline = CaptureDraft();
            characterBaseline = CaptureCharacters();
            await ApplyGenerationAsync(generation, lifetime.Token);
            activeTab = StudioTab.Preview;
            allowNavigation = true;
            Navigation.NavigateTo(ProjectId is Guid id
                ? $"/projects/{id:D}/boards/{generation.GenerationId:D}"
                : $"/boards/new?generationId={generation.GenerationId:D}");
            allowNavigation = false;
        }
        catch (AccessTokenNotAvailableException exception) { exception.Redirect(); }
        catch (Exception exception) when (exception is GenerationApiException or HttpRequestException) { workspaceError = exception.Message; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { isSubmitting = false; }
    }

    private async Task RefreshGenerationAsync()
    {
        if (activeGenerationId is not Guid id) return;
        if (isRefreshing) { refreshRequested = true; return; }
        var token = boardCancellation?.Token ?? lifetime.Token;
        isRefreshing = true;
        try
        {
            do
            {
                refreshRequested = false;
                var generation = await GenerationClient.GetAsync(id, token);
                if (activeGenerationId != id) return;
                await ApplyGenerationAsync(generation, token);
            } while (refreshRequested && !token.IsCancellationRequested);
        }
        catch (AccessTokenNotAvailableException exception) { exception.Redirect(); }
        catch (Exception exception) when (exception is GenerationApiException or HttpRequestException) { workspaceError = exception.Message; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { isRefreshing = false; }
    }

    private async Task ApplyGenerationAsync(GenerationResponse generation, CancellationToken token)
    {
        workspaceError = null;
        isGenerating = generation.Status is GenerationStatus.Pending or GenerationStatus.Processing;
        generatedPanelCount = generation.CompletedPanelCount;
        progressMessage = generation.Status == GenerationStatus.Pending ? "En attente d'un atelier de génération disponible…" : $"{generatedPanelCount} / {generation.PanelCount} cases illustrées";
        var boardIndex = projectBoards.FindIndex(b => b.GenerationId == generation.GenerationId);
        if (boardIndex >= 0) projectBoards[boardIndex] = projectBoards[boardIndex] with { Status = generation.Status };
        foreach (var status in generation.Panels)
        {
            var panel = panels[status.Number - 1];
            panel.Status = status.Status switch { PanelGenerationStatus.Processing => PanelUiStatus.Generating, PanelGenerationStatus.Completed => PanelUiStatus.Ready, PanelGenerationStatus.Failed => PanelUiStatus.Error, _ => PanelUiStatus.Pending };
            panel.EffectivePrompt = status.EffectivePrompt;
            panel.Error = status.Error;
            if (panel.ImageDataUrl is null && status.ImageUri is not null)
                panel.ImageDataUrl = await GenerationClient.DownloadDataUrlAsync(status.ImageUri, token);
        }
        if (generation.Status == GenerationStatus.Completed && generation.BoardUri is not null && boardDataUrl is null)
            boardDataUrl = await GenerationClient.DownloadDataUrlAsync(generation.BoardUri, token);
        if (generation.Status is GenerationStatus.Failed or GenerationStatus.Canceled)
            workspaceError = generation.Error ?? (generation.Status == GenerationStatus.Failed ? "La planche n'a pas pu être générée. Vous pouvez créer une nouvelle version." : "La génération a été annulée.");
    }

    private bool TryGetNotifiedPanel(PanelNotification n, out ComicPanelDraft panel)
    {
        if (Guid.TryParse(n.BoardId, out var id) && id == activeGenerationId && n.PanelNumber > 0 && n.PanelNumber <= panels.Count)
        { panel = panels[n.PanelNumber - 1]; return true; }
        panel = null!;
        return false;
    }

    private async Task HandlePanelCompletedAsync(PanelNotification n)
    {
        if (!TryGetNotifiedPanel(n, out var panel)) return;
        var token = boardCancellation?.Token ?? lifetime.Token;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await RefreshGenerationAsync();
            if (!TryGetNotifiedPanel(n, out panel) || panel.ImageDataUrl is not null || panel.Status == PanelUiStatus.Error) return;
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
        Notify(NotificationSeverity.Warning, "Image en retard", $"La case {n.PanelNumber} n'est pas encore disponible. Utilisez Actualiser la planche.");
    }

    private async Task ChangeTemplateAsync(ChangeEventArgs args)
    {
        if (!Enum.TryParse<BoardTemplate>(args.Value?.ToString(), out var template) || template == selectedTemplate) return;
        if (panels.Any(p => !string.IsNullOrWhiteSpace(p.Prompt)) && !await ConfirmAsync("Changer de mise en page réinitialise les descriptions des cases. Continuer ?")) return;
        selectedTemplate = template;
        panels.Clear();
        panels.AddRange(Enumerable.Range(0, BoardTemplateDefinitions.Get(template).Slots.Count).Select(_ => new ComicPanelDraft()));
        activeGenerationId = null;
        boardDataUrl = null;
        selectedPanel = generatedPanelCount = 0;
    }

    private string PanelSlotStyle(int index)
    {
        var slot = DisplayTemplateDefinition.Slots[index];
        return $"grid-column:{slot.Column + 1}/span {slot.ColumnSpan};grid-row:{slot.Row + 1}/span {slot.RowSpan};";
    }
    private void OpenImageViewer(string imageUrl, string alt)
    {
        viewerImageUrl = imageUrl;
        viewerImageAlt = alt;
        viewerOpening = true;
    }
    private void OpenBoardImageViewer()
    {
        if (boardDataUrl is not null) OpenImageViewer(boardDataUrl, "Planche de bande dessinée générée");
    }
    private void OpenPanelImageViewer(int index)
    {
        if (panels[index].ImageDataUrl is { } imageDataUrl) OpenImageViewer(imageDataUrl, $"Case {index + 1} générée");
    }
    private void CloseImageViewer()
    {
        viewerImageUrl = viewerImageAlt = null;
        viewerOpening = false;
    }
    private void SelectPanel(int index) { selectedPanel = index; activeTab = StudioTab.Scenes; }
    private void MovePanel(int offset)
    {
        var next = selectedPanel + offset;
        (panels[selectedPanel], panels[next]) = (panels[next], panels[selectedPanel]);
        selectedPanel = next;
    }

    private async Task AddCharacterAsync()
    {
        if (string.IsNullOrWhiteSpace(newCharacterName)) { workspaceError = "Donnez un nom à votre personnage."; return; }
        isCharacterSaving = true;
        workspaceError = null;
        try
        {
            if (ProjectId is Guid id)
            {
                var created = await GenerationClient.AddCharacterAsync(id, newCharacterName.Trim(), lifetime.Token);
                characters.Add(ToDraft(created.CharacterId, created.Name, created.References));
                UpdateCharacterBaseline(created.CharacterId);
            }
            else characters.Add(new CharacterDraft { Name = newCharacterName.Trim() });
            newCharacterName = string.Empty;
        }
        catch (AccessTokenNotAvailableException exception) { exception.Redirect(); }
        catch (Exception exception) when (exception is GenerationApiException or HttpRequestException) { workspaceError = exception.Message; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { isCharacterSaving = false; }
    }

    private async Task SaveCharacterAsync(CharacterDraft character)
    {
        if (ProjectId is not Guid id) return;
        if (string.IsNullOrWhiteSpace(character.Name)) { workspaceError = "Le nom du personnage ne peut pas être vide."; return; }
        isCharacterSaving = true;
        workspaceError = null;
        try
        {
            var saved = await GenerationClient.UpdateCharacterAsync(id, character.Id, character.Name.Trim(), lifetime.Token);
            character.Name = saved.Name;
            UpdateCharacterBaseline(character.Id);
            Notify(NotificationSeverity.Success, "Personnage enregistré", "Le nom est sauvegardé pour ce projet.");
        }
        catch (AccessTokenNotAvailableException exception) { exception.Redirect(); }
        catch (Exception exception) when (exception is GenerationApiException or HttpRequestException) { workspaceError = exception.Message; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { isCharacterSaving = false; }
    }

    private async Task RemoveCharacterAsync(CharacterDraft character)
    {
        if (!await ConfirmAsync($"Retirer « {character.Name} » du projet et de cette planche ? Les planches précédentes ne sont pas modifiées.")) return;
        isCharacterSaving = true;
        workspaceError = null;
        try
        {
            if (ProjectId is Guid id) await GenerationClient.DeleteCharacterAsync(id, character.Id, lifetime.Token);
            characters.Remove(character);
            savedCharacterNames.Remove(character.Id);
            foreach (var panel in panels) panel.CharacterIds = panel.CharacterIds.Where(id => id != character.Id).ToArray();
            characterBaseline = SerializeCharacterNames(savedCharacterNames);
        }
        catch (AccessTokenNotAvailableException exception) { exception.Redirect(); }
        catch (Exception exception) when (exception is GenerationApiException or HttpRequestException) { workspaceError = exception.Message; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { isCharacterSaving = false; }
    }

    private async Task UploadReferencesAsync(CharacterDraft character, InputFileChangeEventArgs args)
    {
        isCharacterSaving = true;
        workspaceError = null;
        try
        {
            var files = args.GetMultipleFiles(4);
            foreach (var file in files)
            {
                if (file.ContentType is not ("image/png" or "image/jpeg") || file.Size > 10 * 1024 * 1024)
                { workspaceError = "Choisissez au maximum 4 images PNG/JPEG, de 10 Mo maximum chacune."; return; }
            }
            foreach (var file in files)
            {
                var uploaded = ProjectId is Guid id
                    ? await GenerationClient.UploadCharacterReferenceAsync(id, character.Id, file, lifetime.Token)
                    : await GenerationClient.UploadReferenceAsync(file, lifetime.Token);
                character.References.Add(new(uploaded.ReferenceId, uploaded.FileName));
            }
            Notify(NotificationSeverity.Success, "Références envoyées", "Vos images sont stockées dans votre espace privé.");
        }
        catch (AccessTokenNotAvailableException exception) { exception.Redirect(); }
        catch (Exception exception) when (exception is GenerationApiException or HttpRequestException or IOException or InvalidOperationException) { workspaceError = exception.Message; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { isCharacterSaving = false; }
    }

    private readonly Dictionary<string, string> savedCharacterNames = [];
    private void UpdateCharacterBaseline(string id)
    {
        savedCharacterNames[id] = characters.Single(c => c.Id == id).Name;
        characterBaseline = SerializeCharacterNames(savedCharacterNames);
    }
    private string CaptureCharacters() => SerializeCharacterNames(characters.ToDictionary(c => c.Id, c => c.Name));
    private static string SerializeCharacterNames(Dictionary<string, string> names) => JsonSerializer.Serialize(names.OrderBy(pair => pair.Key));
    private string CaptureDraft() => JsonSerializer.Serialize(new
    {
        boardTitle, storyDescription, projectDescription, context, rewritePrompt, selectedTemplate,
        Panels = panels.Select(p => new { p.Prompt, Characters = p.CharacterIds.ToArray() }),
        Characters = ProjectId is null ? characters.Select(c => new { c.Id, c.Name, References = c.References.ToArray() }).ToArray() : null
    });

    private async Task<bool> ConfirmAsync(string message) =>
        await Dialogs.Confirm(message, "Votre histoire", new ConfirmOptions { OkButtonText = "Continuer", CancelButtonText = "Annuler" }) == true;
    private async Task BeforeNavigationAsync(LocationChangingContext context)
    {
        if (HasUnsavedChanges && !await ConfirmAsync("Votre brouillon contient des modifications non sauvegardées. Quitter cette page ?"))
            context.PreventNavigation();
    }
    private void Notify(NotificationSeverity severity, string summary, string detail) =>
        NotificationService.Notify(new NotificationMessage { Severity = severity, Summary = summary, Detail = detail, Duration = 6000 });
    private static string StatusLabel(GenerationStatus status) => status switch
    {
        GenerationStatus.Pending => "En attente", GenerationStatus.Processing => "En cours", GenerationStatus.Completed => "Prête à lire",
        GenerationStatus.Failed => "À relancer", GenerationStatus.Canceled => "Annulée", _ => status.ToString()
    };
    private static string PanelStatusClass(PanelUiStatus status) => status switch
    { PanelUiStatus.Generating => "is-generating", PanelUiStatus.Ready => "is-ready", PanelUiStatus.Error => "has-error", _ => string.Empty };
    private static string PanelStatusLabel(PanelUiStatus status) => status switch
    { PanelUiStatus.Generating => "Illustration en cours…", PanelUiStatus.Ready => "Illustrée", PanelUiStatus.Error => "Image indisponible", _ => "Votre scène, bientôt illustrée" };
    private enum StudioTab { Scenes, Characters, Preview }
    private sealed record PanelNotification(string BoardId, int PanelNumber);
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        boardCancellation?.Cancel();
        boardCancellation?.Dispose();
        if (notificationConnection is not null) await notificationConnection.DisposeAsync();
        lifetime.Dispose();
    }
}
