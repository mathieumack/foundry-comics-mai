using Microsoft.Agents.AI;
using System.ClientModel;

namespace MaiImageGenerator.Api.Services;

public sealed class MafPromptRewriter(AIAgent agent) : IPromptRewriter
{
    internal const string Instructions = """
        You are an art director specializing in image-generation prompts. Rewrite the supplied context and panel
        description into one autonomous prompt. Preserve the requested characters, action, framing, visual style,
        displayed text, dialogue, and constraints. Return only the final prompt without Markdown or commentary.
        """;

    public async Task<string> RewriteAsync(
        string? context,
        string prompt,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await agent.RunAsync(
                $"Global context:\n{context ?? "None"}\n\nPanel prompt:\n{prompt}",
                cancellationToken: cancellationToken);
            var rewrittenPrompt = response.ToString().Trim();
            return string.IsNullOrWhiteSpace(rewrittenPrompt)
                ? throw new FoundryGenerationException("The prompt model returned no content.")
                : rewrittenPrompt;
        }
        catch (ClientResultException exception)
        {
            throw new FoundryGenerationException(
                $"Prompt rewriting failed with HTTP status {exception.Status}.",
                exception);
        }
    }
}