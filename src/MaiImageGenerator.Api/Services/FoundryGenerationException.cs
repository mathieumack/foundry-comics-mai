using MaiImageGenerator.Shared.Services;

namespace MaiImageGenerator.Api.Services;

public sealed class FoundryGenerationException : Exception, IImageGenerationRetryAfter
{
    public FoundryGenerationException(string message, int? upstreamStatusCode = null)
        : base(message)
    {
        UpstreamStatusCode = upstreamStatusCode;
    }

    public FoundryGenerationException(
        string message,
        Exception innerException,
        int? upstreamStatusCode = null)
        : base(message, innerException)
    {
        UpstreamStatusCode = upstreamStatusCode;
    }

    public int? UpstreamStatusCode { get; }
    public TimeSpan? RetryAfter { get; init; }
}
