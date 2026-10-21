using System.Diagnostics;
using MaiImageGenerator.Api.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using System.ClientModel;

namespace MaiImageGenerator.Api.Services;

public sealed class MafPromptRewriter(
    AIAgent agent,
    IOptions<FoundryOptions> options,
    ILogger<MafPromptRewriter> logger) : IPromptRewriter
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
        var stopwatch = Stopwatch.StartNew();
        var endpoint = new Uri(options.Value.Endpoint).GetLeftPart(UriPartial.Authority);
        logger.LogInformation(
            "AI operation {OperationName} started for provider {Provider} model {Model} at stage {GenerationStage} using endpoint {UpstreamEndpoint}.",
            "RewritePrompt",
            "AzureFoundry",
            options.Value.PromptModel,
            "PromptRewrite",
            endpoint);
        try
        {
            var response = await agent.RunAsync(
                $"Global context:\n{context ?? "None"}\n\nPanel prompt:\n{prompt}",
                cancellationToken: cancellationToken);
            var rewrittenPrompt = response.ToString().Trim();
            var result = string.IsNullOrWhiteSpace(rewrittenPrompt)
                ? throw new FoundryGenerationException("The prompt model returned no content.")
                : rewrittenPrompt;
            logger.LogInformation(
                "AI operation {OperationName} completed with outcome {Outcome} in {ElapsedMs} ms.",
                "RewritePrompt",
                "Succeeded",
                stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(
                exception,
                "AI operation {OperationName} ended with outcome {Outcome} at stage {GenerationStage} after {ElapsedMs} ms.",
                "RewritePrompt",
                cancellationToken.IsCancellationRequested ? "Canceled" : "TimedOut",
                "PromptRewrite",
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (ClientResultException exception)
        {
            var safeException = new FoundryGenerationException(
                $"Prompt rewriting failed with HTTP status {exception.Status}.");
            logger.LogError(
                safeException,
                "AI operation {OperationName} failed for provider {Provider} model {Model} at stage {GenerationStage} using endpoint {UpstreamEndpoint} with status {UpstreamStatus} and code {SafeErrorCode} after {ElapsedMs} ms.",
                "RewritePrompt",
                "AzureFoundry",
                options.Value.PromptModel,
                "PromptRewrite",
                endpoint,
                exception.Status,
                "ClientResult",
                stopwatch.ElapsedMilliseconds);
            throw safeException;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "AI operation {OperationName} failed at stage {GenerationStage} with code {SafeErrorCode} after {ElapsedMs} ms.",
                "RewritePrompt",
                "PromptRewrite",
                exception.GetType().Name,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}