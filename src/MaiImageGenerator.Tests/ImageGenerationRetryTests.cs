using MaiImageGenerator.Shared.Services;

namespace MaiImageGenerator.Tests;

public sealed class ImageGenerationRetryTests
{
    [Theory]
    [InlineData("Retry-After", "90", 90000)]
    [InlineData("retry-after-ms", "1200", 1200)]
    [InlineData("x-ms-retry-after-ms", "2500", 2500)]
    public void ReadRetryAfter_ParsesProviderDelay(string name, string value, int milliseconds)
    {
        var result = ImageGenerationRetry.ReadRetryAfter(header => header == name ? value : null);

        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), result);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("999999999999999")]
    [InlineData("invalid")]
    public void ReadRetryAfter_IgnoresInvalidValues(string value)
    {
        Assert.Null(ImageGenerationRetry.ReadRetryAfter(_ => value));
    }

    [Fact]
    public void ReadRetryAfter_PrefersMillisecondHeader()
    {
        var result = ImageGenerationRetry.ReadRetryAfter(header => header switch
        {
            "retry-after-ms" => "1234",
            "Retry-After" => "60",
            _ => null
        });

        Assert.Equal(TimeSpan.FromMilliseconds(1234), result);
    }

    [Fact]
    public void ReadRetryAfter_ParsesHttpDate()
    {
        var date = DateTimeOffset.UtcNow.AddMinutes(2).ToString("R");
        var result = ImageGenerationRetry.ReadRetryAfter(header => header == "Retry-After" ? date : null);

        Assert.NotNull(result);
        Assert.InRange(result.Value.TotalSeconds, 118, 120);
    }
}
