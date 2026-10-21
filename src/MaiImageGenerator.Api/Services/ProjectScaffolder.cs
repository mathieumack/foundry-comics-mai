using System.Diagnostics;
using System.ClientModel;
using System.Text.Json;
using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Api.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace MaiImageGenerator.Api.Services;

public sealed class ProjectScaffolder
{
    internal const string Instructions = """
        You are a comic project editor. Expand a story description into a coherent storyboard.
        Return only one JSON object with this exact shape:
        {"title":"...","description":"...","context":"...","panelPrompts":["..."]}
        The description summarizes the complete project. The context is a shared visual style and art direction prompt.
        Each panel prompt must independently describe its framing, setting, characters, action, and any dialogue or
        displayed text while advancing the story in reading order. Return exactly the requested number of panel prompts.
        Do not include Markdown, code fences, commentary, or properties other than those specified.
        """;

    private readonly AIAgent _agent;
    private readonly FoundryOptions _options;
    private readonly ILogger<ProjectScaffolder> _logger;

    public ProjectScaffolder(
        OpenAIClient client,
        IOptions<FoundryOptions> options,
        ILogger<ProjectScaffolder> logger)
    {
        _options = options.Value;
        _logger = logger;
        _agent = client.GetChatClient(options.Value.PromptModel).AsAIAgent(
            instructions: Instructions,
            name: "ProjectScaffolder");
    }

    public async Task<ProjectScaffoldResponse> CreateAsync(
        CreateProjectScaffoldRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.StoryDescription) || request.StoryDescription.Length > 10_000)
        {
            throw new ArgumentException("A story description between 1 and 10,000 characters is required.");
        }

        if (request.PanelCount is < 1 or > 8)
        {
            throw new ArgumentException("A project scaffold requires between 1 and 8 panels.");
        }

        var stopwatch = Stopwatch.StartNew();
        var endpoint = new Uri(_options.Endpoint).GetLeftPart(UriPartial.Authority);
        _logger.LogInformation(
            "AI operation {OperationName} started for provider {Provider} model {Model} at stage {GenerationStage} using endpoint {UpstreamEndpoint}.",
            "CreateProjectScaffold",
            "AzureFoundry",
            _options.PromptModel,
            "ProjectScaffold",
            endpoint);
        try
        {
            var response = await _agent.RunAsync(
                $"Panel count: {request.PanelCount}\n\nStory description:\n{request.StoryDescription.Trim()}",
                cancellationToken: cancellationToken);
            var result = Parse(response.ToString(), request.PanelCount);
            _logger.LogInformation(
                "AI operation {OperationName} completed with outcome {Outcome} in {ElapsedMs} ms.",
                "CreateProjectScaffold",
                "Succeeded",
                stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException exception)
        {
            _logger.LogWarning(
                exception,
                "AI operation {OperationName} ended with outcome {Outcome} at stage {GenerationStage} after {ElapsedMs} ms.",
                "CreateProjectScaffold",
                cancellationToken.IsCancellationRequested ? "Canceled" : "TimedOut",
                "ProjectScaffold",
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (ClientResultException exception)
        {
            var safeException = new FoundryGenerationException(
                $"Project scaffolding failed with HTTP status {exception.Status}.");
            _logger.LogError(
                safeException,
                "AI operation {OperationName} failed for provider {Provider} model {Model} at stage {GenerationStage} using endpoint {UpstreamEndpoint} with status {UpstreamStatus} and code {SafeErrorCode} after {ElapsedMs} ms.",
                "CreateProjectScaffold",
                "AzureFoundry",
                _options.PromptModel,
                "ProjectScaffold",
                endpoint,
                exception.Status,
                "ClientResult",
                stopwatch.ElapsedMilliseconds);
            throw safeException;
        }
        catch (JsonException exception)
        {
            _logger.LogError(
                exception,
                "AI operation {OperationName} failed at stage {GenerationStage} with code {SafeErrorCode} after {ElapsedMs} ms.",
                "CreateProjectScaffold",
                "ResponseValidation",
                "InvalidResponse",
                stopwatch.ElapsedMilliseconds);
            throw new FoundryGenerationException("The prompt model returned an invalid project scaffold.", exception);
        }
    }

    private static ProjectScaffoldResponse Parse(string content, int panelCount)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new JsonException("The response did not contain a JSON object.");
        }

        var scaffold = JsonSerializer.Deserialize<ProjectScaffoldResponse>(
            content[start..(end + 1)],
            JsonSerializerOptions.Web);
        if (scaffold is null ||
            string.IsNullOrWhiteSpace(scaffold.Title) ||
            string.IsNullOrWhiteSpace(scaffold.Description) ||
            string.IsNullOrWhiteSpace(scaffold.Context) ||
            scaffold.PanelPrompts is null ||
            scaffold.PanelPrompts.Count != panelCount ||
            scaffold.PanelPrompts.Any(string.IsNullOrWhiteSpace))
        {
            throw new JsonException("The response did not contain all required scaffold values.");
        }

        return scaffold with
        {
            Title = scaffold.Title.Trim(),
            Description = scaffold.Description.Trim(),
            Context = scaffold.Context.Trim(),
            PanelPrompts = scaffold.PanelPrompts.Select(prompt => prompt.Trim()).ToArray()
        };
    }
}
