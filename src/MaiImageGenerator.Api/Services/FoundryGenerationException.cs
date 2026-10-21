namespace MaiImageGenerator.Api.Services;

public sealed class FoundryGenerationException : Exception
{
    public int? Status { get; }

    public FoundryGenerationException(string message)
        : base(message)
    {
    }

    public FoundryGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FoundryGenerationException(string message, Exception innerException, int status)
        : base(message, innerException)
    {
        Status = status;
    }
}