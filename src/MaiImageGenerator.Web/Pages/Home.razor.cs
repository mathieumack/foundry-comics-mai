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
    private Task statusRefreshTask = Task.CompletedTask;
    private CancellationTokenSource? boardCancellation;
    private Guid? activeGenerationId, loadedProjectId, loadedGenerationId;
    private ElementReference imageViewerDialog;
    private ProjectResponse? project;
    private GenerationResponse? generationResult;
    private IReadOnlyList<AiGenerationTrace>? scaffoldTraces;
    private Guid? scaffoldId;
    private string boardTitle = "Nouvelle planche", newCharacterName = string.Empty;
    private string? storyDescription, projectDescription, context, boardDataUrl, progressMessage, projectError, workspaceError, viewerImageUrl, viewerImageAlt;
    private string baseline = string.Empty, characterBaseline = string.Empty;
    private int generatedPanelCount, selectedPanel;
    private BoardTemplate selectedTemplate;
    private StudioTab activeTab;
    private bool initialized, rewritePrompt = true, isGenerating, isSubmitting, isRefreshing, isScaffolding, isProjectLoading, isBoardLoading = true, isCharacterSaving;
    private bool refreshRequested, allowNavigation, viewerOpening;
    private bool EditingLocked => isGenerating || isSubmitting || isScaffolding || isCharacterSaving || isBoardLoading || isPanelCommandSubmitting;
    private bool HasUnsavedChanges => !allowNavigation && initialized && (isScaffolding || isCharacterSaving || !string.IsNullOrWhiteSpace(newCharacterName) || revisedPanelPrompts.Values.Any(value => !string.IsNullOrWhiteSpace(value)) || CaptureDraft() != baseline || CaptureCharacters() != characterBaseline);
    private BoardTemplateDefinition DisplayTemplateDefinition => BoardTemplateDefinitions.Resolve(selectedTemplate, panels.Count);
    private string BoardGridStyle => $"grid-template-columns:repeat({DisplayTemplateDefinition.Columns},minmax(0,1fr));";
    private bool HasPendingArtifacts => generationResult is { } result &&
        (result.BoardUri is not null && (boardDataUrl is null || loadedBoardUri != result.BoardUri) ||
         result.Panels.Any(panel => panel.ImageUri is not null &&
             (PanelForGenerationNumber(panel.Number).ImageDataUrl is null || PanelForGenerationNumber(panel.Number).LoadedImageUri != panel.ImageUri)));
    private GenerationCompleteness ResultCompleteness => generationResult?.Completeness ??
        (generationResult?.Panels.Count(panel => panel.Status == PanelGenerationStatus.Completed && panel.ImageUri is not null) is not > 0
            ? GenerationCompleteness.None
            : generationResult.Panels.All(panel => panel.Status == PanelGenerationStatus.Completed && panel.ImageUri is not null)
                ? GenerationCompleteness.Complete : GenerationCompleteness.Partial);
    private int ResultFailedPanelCount => generationResult is { } result
        ? result.Completeness is null
            ? result.Panels.Count(panel => panel.Status == PanelGenerationStatus.Failed)
            : result.FailedPanelCount
        : 0;
    private string ResultLabel => ResultCompleteness switch
    {
        GenerationCompleteness.Complete => "Résultat complet",
        GenerationCompleteness.Partial => "Résultat partiel",
        _ => isGenerating ? "Aucune image disponible pour le moment" : "Aucune image utilisable"
    };

    protected override async Task OnInitializedAsync()
    {
        statusRefreshTask = RefreshActiveGenerationPeriodicallyAsync();
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
        notificationConnection.On<PanelNotification>("PanelGenerationStarted", n => InvokeAsync(() => HandlePanelStartedAsync(n)));
        notificationConnection.On<PanelNotification>("PanelGenerationCompleted", n => InvokeAsync(() => HandlePanelCompletedAsync(n)));
        notificationConnection.Reconnected += _ => InvokeAsync(RefreshGenerationAsync);
        try { await notificationConnection.StartAsync(lifetime.Token); }
        catch (AccessTokenNotAvailableException exception) { exception.Redirect(); }
        catch (Exception exception) when (exception is HttpRequestException or HubException)
        {
            Notify(NotificationSeverity.Warning, "Suivi en direct indisponible", $"{exception.Message} L'avancement sera actualisé automatiquement.");
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
                    GenerationPanelNumber = p.Number,
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
        ResetPanelVersionState();
        activeGenerationId = null;
        generationResult = null;
        scaffoldTraces = null;
        scaffoldId = null;
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
        scaffoldTraces = project.ScaffoldTraces;
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
        if (panels.Any(p => !string.IsNullOrWhiteSpace(p.Prompt)) && !await ConfirmAsync("Remplacer vos descriptions et consignes révisées actuelles par de nouvelles propositions ?")) return;
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
        ResetPanelVersionState();
        boardTitle = preserveTitle ? project!.Title : scaffold.Title;
        projectDescription = scaffold.Description;
        context = preserveTitle ? project!.Context : scaffold.Context;
        scaffoldTraces = scaffold.Traces;
        if (preserveTitle)
        {
            // Wizard scaffolds already belong to the saved project, not its first board.
            scaffoldId = null;
            scaffoldTraces = project!.ScaffoldTraces is { Count: > 0 } savedTraces ? savedTraces : scaffold.Traces;
        }
        else
        {
            scaffoldId = scaffold.ScaffoldId;
        }
        panels.Clear();
        panels.AddRange(scaffold.PanelPrompts.Select(prompt => new ComicPanelDraft { Prompt = prompt }));
        selectedPanel = 0;
        boardDataUrl = null;
        activeGenerationId = null;
        generationResult = null;
    }

    private async Task GenerateBoardAsync()
    {
        if (EditingLocked || isRefreshing) return;
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
        if (revisedPanelPrompts.Values.Any(value => !string.IsNullOrWhiteSpace(value)) &&
            !await ConfirmAsync("Les consignes révisées des versions de cases ne font pas partie de cette nouvelle planche et seront effacées du brouillon. Continuer ?")) return;
        if (EditingLocked || isRefreshing) return;
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
                    new CreateBoardRequest(boardTitle.Trim(), projectDescription?.Trim(), rewritePrompt, selectedTemplate, requests, scaffoldId), lifetime.Token);
                projectBoards.Insert(0, new(generation.GenerationId, generation.Title, generation.CreatedAt, generation.Status, null));
            }
            else
            {
                generation = await GenerationClient.CreateAsync(new CreateGenerationRequest(boardTitle.Trim(), projectDescription?.Trim(),
                    context?.Trim(), rewritePrompt, selectedTemplate, requests,
                    characters.Select(c => new CharacterRequest(c.Id, c.Name.Trim(), c.References.Select(r => r.ReferenceId).ToArray())).ToArray(), scaffoldId), lifetime.Token);
            }
            scaffoldId = null;
            activeGenerationId = generation.GenerationId;
            loadedGenerationId = activeGenerationId;
            boardDataUrl = null;
            ResetPanelVersionState();
            foreach (var panel in panels) { panel.LoadedImageUri = null; panel.ImageDataUrl = panel.EffectivePrompt = panel.Error = null; panel.Traces = null; panel.Issue = null; panel.Status = PanelUiStatus.Pending; }
            for (var index = 0; index < panels.Count; index++) panels[index].GenerationPanelNumber = index + 1;
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
        if (isPanelCommandSubmitting) { refreshRequested = true; return; }
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
        finally
        {
            isRefreshing = false;
            StateHasChanged();
        }
    }

    private async Task RefreshActiveGenerationPeriodicallyAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                await InvokeAsync(async () =>
                {
                    if ((!isGenerating && !HasPendingArtifacts) || isBoardLoading) return;
                    await RefreshGenerationAsync();
                });
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task ApplyGenerationAsync(GenerationResponse generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (generationResult is { } current && current.GenerationId == generation.GenerationId && current.Revision > generation.Revision) return;
        workspaceError = null;
        generationResult = generation;
        scaffoldTraces = generation.ScaffoldTraces is { Count: > 0 } boardTraces
            ? boardTraces
            : project?.ScaffoldTraces;
        isGenerating = generation.Status is GenerationStatus.Pending or GenerationStatus.Processing ||
            generation.Panels.Any(p => p.Versions?.Any(v =>
                v.Status is PanelGenerationStatus.Pending or PanelGenerationStatus.Processing) == true);
        generatedPanelCount = generation.CompletedPanelCount;
        progressMessage = generation.Status == GenerationStatus.Pending ? "En attente d'un atelier de génération disponible…" : $"{generatedPanelCount} / {generation.PanelCount} cases illustrées";
        var boardIndex = projectBoards.FindIndex(b => b.GenerationId == generation.GenerationId);
        if (boardIndex >= 0) projectBoards[boardIndex] = projectBoards[boardIndex] with
        {
            Status = generation.Status, PanelCount = generation.PanelCount,
            CompletedPanelCount = generation.CompletedPanelCount, FailedPanelCount = ResultFailedPanelCount,
            Completeness = ResultCompleteness
        };
        foreach (var status in generation.Panels)
        {
            var panel = PanelForGenerationNumber(status.Number);
            panel.GenerationPanelNumber = status.Number;
            panel.Status = status.Status switch { PanelGenerationStatus.Processing => PanelUiStatus.Generating, PanelGenerationStatus.Completed => PanelUiStatus.Ready, PanelGenerationStatus.Failed => PanelUiStatus.Error, _ => PanelUiStatus.Pending };
            panel.EffectivePrompt = status.EffectivePrompt;
            panel.Issue = status.Issue;
            panel.Error = PanelErrorLabel(status);
            panel.Traces = status.Traces;
        }
        StateHasChanged();
        foreach (var status in generation.Panels)
        {
            var panel = PanelForGenerationNumber(status.Number);
            if (status.ImageUri is not null && (panel.ImageDataUrl is null || panel.LoadedImageUri != status.ImageUri))
            {
                var data = await DownloadArtifactAsync(status.ImageUri, token);
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(generationResult, generation)) return;
                if (data is not null) { panel.ImageDataUrl = data; panel.LoadedImageUri = status.ImageUri; }
                StateHasChanged();
            }
        }
        if (generation.BoardUri is not null && (boardDataUrl is null || loadedBoardUri != generation.BoardUri))
        {
            var data = await DownloadArtifactAsync(generation.BoardUri, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(generationResult, generation)) return;
            if (data is not null) { boardDataUrl = data; loadedBoardUri = generation.BoardUri; }
        }
        if (generation.Status is GenerationStatus.Failed or GenerationStatus.Canceled)
            workspaceError = generation.Error ?? (generation.Status == GenerationStatus.Failed ? "La planche n'a pas pu être générée. Vous pouvez créer une nouvelle version." : "La génération a été annulée.");
    }

    private async Task<string?> DownloadArtifactAsync(Uri uri, CancellationToken token)
    {
        try { return await GenerationClient.DownloadDataUrlAsync(uri, token); }
        catch (Exception exception) when (exception is GenerationApiException or HttpRequestException)
        {
            workspaceError = "Une image n'a pas pu être téléchargée. L'actualisation réessaiera sans modifier le diagnostic sauvegardé.";
            return null;
        }
    }

    private Task HandlePanelStartedAsync(PanelNotification notification) =>
        TryGetNotifiedPanel(notification, out _) ? RefreshGenerationAsync() : Task.CompletedTask;

    private bool TryGetNotifiedPanel(PanelNotification n, out ComicPanelDraft panel)
    {
        if (Guid.TryParse(n.BoardId, out var id) && id == activeGenerationId && n.PanelNumber > 0 && n.PanelNumber <= panels.Count)
        { panel = PanelForGenerationNumber(n.PanelNumber); return true; }
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
        if (panels.Any(p => !string.IsNullOrWhiteSpace(p.Prompt)) && !await ConfirmAsync("Changer de mise en page réinitialise les descriptions et consignes révisées des cases. Continuer ?")) return;
        ResetPanelVersionState();
        selectedTemplate = template;
        panels.Clear();
        panels.AddRange(Enumerable.Range(0, BoardTemplateDefinitions.Get(template).Slots.Count).Select(_ => new ComicPanelDraft()));
        scaffoldTraces = null;
        scaffoldId = null;
        activeGenerationId = null;
        generationResult = null;
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
    private ComicPanelDraft PanelForGenerationNumber(int number) =>
        panels.FirstOrDefault(panel => panel.GenerationPanelNumber == number) ?? panels[number - 1];
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
        GenerationStatus.Pending => "En attente dans la file", GenerationStatus.Processing => "Traitement en cours", GenerationStatus.Completed => "Traitement terminé",
        GenerationStatus.Failed => "Échec technique du traitement", GenerationStatus.Canceled => "Annulée", _ => status.ToString()
    };
    private static string StatusLabel(GenerationSummaryResponse board) => board.Completeness switch
    {
        GenerationCompleteness.Partial => $"Résultat partiel · {board.CompletedPanelCount}/{board.PanelCount} illustrées · {board.FailedPanelCount} en échec",
        GenerationCompleteness.Complete => "Résultat complet",
        GenerationCompleteness.None when board.Status is GenerationStatus.Completed or GenerationStatus.Failed => "Aucune image utilisable",
        _ => StatusLabel(board.Status)
    };
    private static string PanelStatusClass(PanelUiStatus status) => status switch
    { PanelUiStatus.Generating => "is-generating", PanelUiStatus.Ready => "is-ready", PanelUiStatus.Error => "has-error", _ => string.Empty };
    private static string PanelStatusLabel(ComicPanelDraft panel) => panel.Status switch
    {
        PanelUiStatus.Ready => "Illustrée",
        _ when panel.Issue == PanelGenerationIssue.ProviderThrottled => "Limite du fournisseur atteinte",
        PanelUiStatus.Error when panel.Issue == PanelGenerationIssue.ContentSafetyRejected => "Refus de sécurité du contenu",
        PanelUiStatus.Error => "Échec technique de l'image",
        PanelUiStatus.Generating => "Illustration en cours…",
        _ => "En attente de traitement"
    };
    private static string? PanelErrorLabel(PanelGenerationResponse panel) => panel.Issue switch
    {
        PanelGenerationIssue.ContentSafetyRejected => "Image refusée par la sécurité du contenu. Revoyez la scène et les références avant de créer une nouvelle version.",
        PanelGenerationIssue.ProviderThrottled when panel.Status == PanelGenerationStatus.Processing => "Le fournisseur limite les requêtes. Attente avant une nouvelle tentative automatique.",
        PanelGenerationIssue.ProviderThrottled => "Le fournisseur est occupé. Créez une nouvelle version plus tard.",
        _ when panel.Status == PanelGenerationStatus.Failed => "Échec technique de cette image. Vous pouvez générer une nouvelle version.",
        _ => null
    };
    private enum StudioTab { Scenes, Characters, Preview }
    private sealed record PanelNotification(string BoardId, int PanelNumber);
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        boardCancellation?.Cancel();
        await statusRefreshTask;
        boardCancellation?.Dispose();
        if (notificationConnection is not null) await notificationConnection.DisposeAsync();
        lifetime.Dispose();
    }
}
