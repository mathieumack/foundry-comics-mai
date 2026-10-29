using MaiImageGenerator.Api.Configuration;
using MaiImageGenerator.Shared.Data.Entities;
using MaiImageGenerator.Shared.Generation;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace MaiImageGenerator.Api.Services;

public sealed class MafPromptRewriter(OpenAIClient client, IOptions<FoundryOptions> options) : IPromptRewriter
{
    internal const string Instructions = """
        You are an art director specializing in image-generation prompts. Rewrite the supplied context and panel
        description into one autonomous prompt. Preserve the requested characters, action, framing, visual style,
        displayed text, dialogue, and constraints. Return only the final prompt without Markdown or commentary.
        """;

    public async Task<string> RewriteAsync(
        string? context,
        string prompt,
        CancellationToken cancellationToken,
        AiGenerationTrace? trace = null)
    {
        trace ??= new AiGenerationTrace();
        using var capture = AiTraceCapture.Begin(trace, "prompt-rewrite", options.Value.PromptModel);
        try
        {
            var agent = new FoundryChatTraceClient(client.GetChatClient(options.Value.PromptModel), trace)
                .AsAIAgent(instructions: Instructions, name: "PromptRewriter");
            var response = await agent.RunAsync(
                $"Global context:\n{context ?? "None"}\n\nPanel prompt:\n{prompt}",
                cancellationToken: cancellationToken);
            var rewrittenPrompt = response.ToString().Trim();
            if (string.IsNullOrWhiteSpace(rewrittenPrompt))
            {
                throw new FoundryGenerationException("The prompt model returned no content.");
            }

            capture.Complete();
            return rewrittenPrompt;
        }
        catch (OperationCanceledException)
        {
            capture.Cancel();
            throw;
        }
        catch (ClientResultException exception)
        {
            throw new FoundryGenerationException(
                $"Prompt rewriting failed with HTTP status {exception.Status}.",
                exception);
        }
    }
}