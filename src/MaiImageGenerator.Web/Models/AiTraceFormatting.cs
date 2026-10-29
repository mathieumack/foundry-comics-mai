using System.Globalization;

namespace MaiImageGenerator.Web.Models;

public static class AiTraceFormatting
{
    public static bool IsImage(AiGenerationTrace trace) =>
        trace.Operation.Contains("image", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<AiGenerationTrace> Select(
        IReadOnlyList<AiGenerationTrace>? traces, bool? images = null) =>
        traces?.Where(trace => images is null || IsImage(trace) == images.Value).ToArray() ?? [];

    public static string Summary(AiGenerationTrace trace)
    {
        var status = string.Equals(trace.Status, "completed", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" · {Value(trace.Status, "status unavailable")}";
        var image = IsImage(trace) ? $" · {ImageSummary(trace)}" : string.Empty;
        return $"{Value(trace.Model, "model unavailable")}{status}{image} · {Usage(trace)}";
    }

    public static string Usage(AiGenerationTrace trace)
    {
        var source = UsageSource(trace);
        if (source == "unavailable")
            return "tokens unavailable";

        var counts = new List<string>();
        if (trace.TotalTokens is >= 0)
            counts.Add($"{Number(trace.TotalTokens.Value)} tokens");
        else
        {
            if (trace.InputTokens is >= 0) counts.Add($"{Number(trace.InputTokens.Value)} input tokens");
            if (trace.OutputTokens is >= 0) counts.Add($"{Number(trace.OutputTokens.Value)} output tokens");
        }

        return counts.Count == 0 ? "tokens unavailable" : $"{string.Join(" · ", counts)} ({source})";
    }

    public static string UsageSource(AiGenerationTrace trace) => trace.UsageSource.ToLowerInvariant() switch
    {
        "provider-reported" => "provider-reported",
        "estimated" => "estimated",
        "unavailable" or "" => "unavailable",
        _ => $"source: {trace.UsageSource}"
    };

    public static string ImageSummary(AiGenerationTrace trace)
    {
        var hasReportedCount = trace.ImageCount is >= 0;
        var count = hasReportedCount ? trace.ImageCount!.Value : 1;
        var qualifier = string.Equals(trace.Status, "completed", StringComparison.OrdinalIgnoreCase)
            ? ""
            : hasReportedCount ? " reported" : " requested";
        var size = Value(trace.ImageSize, "1024×1024").Replace("x", "×", StringComparison.OrdinalIgnoreCase);
        return $"{count} {(count == 1 ? "image" : "images")}{qualifier} · {size}";
    }

    public static IReadOnlyList<KeyValuePair<string, string>> Details(AiGenerationTrace trace)
    {
        var details = new List<KeyValuePair<string, string>>
        {
            new("Operation", Value(trace.Operation, "unavailable")),
            new("Provider", Value(trace.Provider, "unavailable")),
            new("Model", Value(trace.Model, "unavailable")),
            new("Status", Value(trace.Status, "unavailable")),
            new("Usage source", UsageSource(trace)),
            new("Input tokens", TokenDetail(trace, trace.InputTokens)),
            new("Output tokens", TokenDetail(trace, trace.OutputTokens)),
            new("Total tokens", TokenDetail(trace, trace.TotalTokens)),
            new("Request ID", Value(trace.RequestId, "unavailable")),
            new("Started", trace.StartedAt == default ? "unavailable" : trace.StartedAt.ToString("O", CultureInfo.InvariantCulture)),
            new("Completed", trace.CompletedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "unavailable"),
            new("Duration", trace.DurationMs is >= 0 ? $"{Number(trace.DurationMs.Value)} ms" : "unavailable")
        };
        if (IsImage(trace))
        {
            details.Add(new("Image request", ImageSummary(trace)));
            details.Add(new("Image quality", Value(trace.ImageQuality, "unavailable")));
        }
        return details;
    }

    private static string TokenDetail(AiGenerationTrace trace, long? count) =>
        UsageSource(trace) != "unavailable" && count is >= 0 ? Number(count.Value) : "unavailable";

    private static string Number(long number) => number.ToString("N0", CultureInfo.InvariantCulture);
    private static string Value(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
