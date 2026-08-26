namespace MaiImageGenerator.Api.Services;

public sealed class FoundryGenerationException : Exception
{
    public FoundryGenerationException(string message)
        : base(message)
    {
    }

    public FoundryGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}