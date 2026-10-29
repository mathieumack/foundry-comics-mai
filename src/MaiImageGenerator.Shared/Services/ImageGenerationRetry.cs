using System.Globalization;

namespace MaiImageGenerator.Shared.Services;

public interface IImageGenerationRetryAfter
{
    TimeSpan? RetryAfter { get; }
}

public static class ImageGenerationRetry
{
    public static TimeSpan? ReadRetryAfter(Func<string, string?> getHeader)
    {
        foreach (var header in new[] { "retry-after-ms", "x-ms-retry-after-ms", "Retry-After" })
        {
            var value = getHeader(header);
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                var milliseconds = header == "Retry-After" ? number * 1000 : number;
                if (double.IsFinite(milliseconds) && milliseconds > 0 && milliseconds <= int.MaxValue)
                {
                    return TimeSpan.FromMilliseconds(milliseconds);
                }
            }
            else if (header == "Retry-After" &&
                     DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            {
                var delay = date - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero && delay.TotalMilliseconds <= int.MaxValue)
                {
                    return delay;
                }
            }
        }

        return null;
    }
}
