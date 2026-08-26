namespace MaiImageGenerator.Api.Services;

public interface IPromptRewriter
{
    Task<string> RewriteAsync(string? context, string prompt, CancellationToken cancellationToken);
}