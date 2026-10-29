using System.Net.Http.Json;
using MaiImageGenerator.Web.Models;
using Microsoft.AspNetCore.Components.Forms;

namespace MaiImageGenerator.Web.Services;

public sealed class GenerationApiClient(HttpClient httpClient)
{
    public async Task<ProjectResponse> CreateProjectAsync(
        CreateProjectRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("api/projects", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ProjectResponse>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no project.");
    }

    public async Task<IReadOnlyList<ProjectResponse>> ListProjectsAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync("api/projects", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ProjectResponse[]>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no projects list.");
    }

    public async Task<ProjectResponse> GetProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"api/projects/{projectId:D}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ProjectResponse>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no project.");
    }

    public async Task<ProjectResponse> UpdateProjectAsync(
        Guid projectId,
        CreateProjectRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync(
            $"api/projects/{projectId:D}",
            request,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ProjectResponse>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no project.");
    }

    public async Task DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.DeleteAsync($"api/projects/{projectId:D}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<BookPlanResponse> GetBookPlanAsync(Guid projectId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"api/projects/{projectId:D}/book/plan", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<BookPlanResponse>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no book plan.");
    }

    public async Task<ProjectCharacterResponse> AddCharacterAsync(
        Guid projectId, string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"api/projects/{projectId:D}/characters", new ProjectCharacterRequest(name), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ProjectCharacterResponse>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no character.");
    }

    public async Task<ProjectCharacterResponse> UpdateCharacterAsync(
        Guid projectId, string characterId, string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync(
            $"api/projects/{projectId:D}/characters/{Uri.EscapeDataString(characterId)}",
            new ProjectCharacterRequest(name), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ProjectCharacterResponse>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no character.");
    }

    public async Task DeleteCharacterAsync(Guid projectId, string characterId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.DeleteAsync(
            $"api/projects/{projectId:D}/characters/{Uri.EscapeDataString(characterId)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<ReferenceUploadResponse> UploadCharacterReferenceAsync(
        Guid projectId, string characterId, IBrowserFile file, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        await using var source = file.OpenReadStream(10 * 1024 * 1024, cancellationToken);
        using var content = new StreamContent(source);
        content.Headers.ContentType = new(file.ContentType);
        form.Add(content, "file", file.Name);
        using var response = await httpClient.PostAsync(
            $"api/projects/{projectId:D}/characters/{Uri.EscapeDataString(characterId)}/references", form, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ReferenceUploadResponse>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no reference metadata.");
    }

    public async Task<BookPlanResponse> GenerateBookPlanAsync(
        Guid projectId,
        GenerateBookPlanRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"api/projects/{projectId:D}/book/plan/generate",
            request,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<BookPlanResponse>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no book plan.");
    }

    public async Task<BookPlanResponse> UpdateBookPlanAsync(
        Guid projectId,
        UpdateBookPlanRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync(
            $"api/projects/{projectId:D}/book/plan",
            request,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<BookPlanResponse>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no book plan.");
    }

    public Task<BookPlanResponse> GenerateChapterProseAsync(
        Guid projectId,
        string chapterId,
        CancellationToken cancellationToken) =>
        GenerateBookPartAsync(
            $"api/projects/{projectId:D}/book/chapters/{Uri.EscapeDataString(chapterId)}/prose/generate",
            cancellationToken);

    public Task<BookPlanResponse> GenerateChapterImageAsync(
        Guid projectId,
        string chapterId,
        CancellationToken cancellationToken) =>
        GenerateBookPartAsync(
            $"api/projects/{projectId:D}/book/chapters/{Uri.EscapeDataString(chapterId)}/image/generate",
            cancellationToken);

    public async Task<IReadOnlyList<GenerationSummaryResponse>> ListBoardsAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"api/projects/{projectId:D}/boards", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<GenerationSummaryResponse[]>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no boards list.");
    }

    public async Task<GenerationResponse> CreateBoardAsync(
        Guid projectId,
        CreateBoardRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"api/projects/{projectId:D}/boards",
            request,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<GenerationResponse>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no generation result.");
    }

    public async Task<ReferenceUploadResponse> UploadReferenceAsync(
        IBrowserFile file,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        await using var source = file.OpenReadStream(10 * 1024 * 1024, cancellationToken);
        using var content = new StreamContent(source);
        content.Headers.ContentType = new(file.ContentType);
        form.Add(content, "file", file.Name);
        using var response = await httpClient.PostAsync("api/references", form, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ReferenceUploadResponse>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no reference metadata.");
    }

    public async Task<GenerationResponse> CreateAsync(
        CreateGenerationRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("api/generations", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<GenerationResponse>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no generation result.");
    }

    public async Task<ProjectScaffoldResponse> CreateScaffoldAsync(
        CreateProjectScaffoldRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("api/generations/scaffold", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ProjectScaffoldResponse>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no project scaffold.");
    }

    public async Task<GenerationResponse> GetAsync(Guid generationId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"api/generations/{generationId:D}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<GenerationResponse>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no generation status.");
    }

    public async Task<IReadOnlyList<GenerationSummaryResponse>> ListAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync("api/generations", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<GenerationSummaryResponse[]>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no generations list.");
    }

    public async Task<string> DownloadDataUrlAsync(Uri relativeUri, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(relativeUri, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/png";
        return $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var details = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new GenerationApiException(
            $"The API returned {(int)response.StatusCode} ({response.ReasonPhrase}). {details}");
    }

    private async Task<BookPlanResponse> GenerateBookPartAsync(
        string relativeUri,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(relativeUri, null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<BookPlanResponse>(cancellationToken)
            ?? throw new GenerationApiException("The API returned no book plan.");
    }
}

public sealed class GenerationApiException(string message) : Exception(message);