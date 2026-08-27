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

    public ProjectScaffolder(OpenAIClient client, IOptions<FoundryOptions> options)
    {
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

        try
        {
            var response = await _agent.RunAsync(
                $"Panel count: {request.PanelCount}\n\nStory description:\n{request.StoryDescription.Trim()}",
                cancellationToken: cancellationToken);
            return Parse(response.ToString(), request.PanelCount);
        }
        catch (ClientResultException exception)
        {
            throw new FoundryGenerationException(
                $"Project scaffolding failed with HTTP status {exception.Status}.",
                exception);
        }
        catch (JsonException exception)
        {
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
