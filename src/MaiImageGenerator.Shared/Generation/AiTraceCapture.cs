using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using MaiImageGenerator.Shared.Data.Entities;

namespace MaiImageGenerator.Shared.Generation;

public static class AiTraceCapture
{
    public const string MeterName = "MaiImageGenerator.AiTraceCapture";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> MetadataEvents = Meter.CreateCounter<long>("ai.trace.metadata");
    private static readonly string[] RequestIdHeaders = ["x-request-id", "apim-request-id", "x-ms-request-id"];

    public static AiGenerationTrace Clone(AiGenerationTrace trace) => new()
    {
        Operation = trace.Operation,
        Provider = trace.Provider,
        Model = trace.Model,
        RequestId = trace.RequestId,
        InputTokens = trace.InputTokens,
        OutputTokens = trace.OutputTokens,
        TotalTokens = trace.TotalTokens,
        UsageSource = trace.UsageSource,
        StartedAt = trace.StartedAt,
        CompletedAt = trace.CompletedAt,
        DurationMs = trace.DurationMs,
        ImageCount = trace.ImageCount,
        ImageSize = trace.ImageSize,
        ImageQuality = trace.ImageQuality,
        Status = trace.Status
    };

    public static CaptureScope Begin(
        AiGenerationTrace trace,
        string operation,
        string model,
        string? imageSize = null,
        string? imageQuality = null)
    {
        trace.Operation = string.IsNullOrWhiteSpace(trace.Operation) ? operation : trace.Operation;
        trace.Model = model;
        trace.StartedAt = DateTimeOffset.UtcNow;
        trace.CompletedAt = null;
        trace.DurationMs = null;
        trace.Status = "started";
        trace.ImageSize = imageSize;
        trace.ImageQuality = imageQuality;
        return new CaptureScope(trace);
    }

    public static void CaptureRequestId(AiGenerationTrace trace, Func<string, string?> readHeader)
    {
        try
        {
            foreach (var header in RequestIdHeaders)
            {
                if (SafeRequestId(readHeader(header)) is { } requestId)
                {
                    trace.RequestId = requestId;
                    break;
                }
            }
        }
        catch (Exception)
        {
            RecordMetadataEvent(trace, "capture-failed");
        }
    }

    public static void CaptureImageResponse(AiGenerationTrace trace, Func<JsonDocument> readResponse)
        => CaptureResponse(trace, readResponse, image: true);

    public static void CaptureChatResponse(AiGenerationTrace trace, Func<JsonDocument> readResponse)
        => CaptureResponse(trace, readResponse, image: false);

    public static void ReportMetadataFailure(AiGenerationTrace trace) =>
        RecordMetadataEvent(trace, "capture-failed");

    private static void CaptureResponse(AiGenerationTrace trace, Func<JsonDocument> readResponse, bool image)
    {
        try
        {
            using var document = readResponse();
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                RecordMetadataEvent(trace, "usage-unavailable");
                return;
            }

            if (image && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                trace.ImageCount = data.GetArrayLength();
            }
            if (!image && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                trace.RequestId ??= SafeRequestId(id.GetString());
            }

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                SetUsage(
                    trace,
                    ReadCount(usage, "input_tokens") ?? ReadCount(usage, "prompt_tokens"),
                    ReadCount(usage, "output_tokens") ?? ReadCount(usage, "completion_tokens"),
                    ReadCount(usage, "total_tokens"));
            }
            else
            {
                RecordMetadataEvent(trace, "usage-unavailable");
            }
        }
        catch (Exception)
        {
            RecordMetadataEvent(trace, "capture-failed");
        }
    }

    private static long? ReadCount(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var count) &&
        count >= 0
            ? count
            : null;

    private static void SetUsage(AiGenerationTrace trace, long? input, long? output, long? total)
    {
        trace.InputTokens = input >= 0 ? input : null;
        trace.OutputTokens = output >= 0 ? output : null;
        trace.TotalTokens = total >= 0 ? total : null;
        trace.UsageSource = trace.InputTokens.HasValue || trace.OutputTokens.HasValue || trace.TotalTokens.HasValue
            ? "provider-reported"
            : "unavailable";
        if (trace.UsageSource == "unavailable")
        {
            RecordMetadataEvent(trace, "usage-unavailable");
        }
    }

    private static string? SafeRequestId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 200 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')
            ? value
            : null;

    private static void RecordMetadataEvent(AiGenerationTrace trace, string outcome) =>
        MetadataEvents.Add(1, new KeyValuePair<string, object?>("operation", trace.Operation),
            new KeyValuePair<string, object?>("outcome", outcome));

    public sealed class CaptureScope : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly AiGenerationTrace _trace;

        internal CaptureScope(AiGenerationTrace trace) => _trace = trace;

        public void Complete() => _trace.Status = "completed";

        public void Cancel() => _trace.Status = "canceled";

        public void Dispose()
        {
            if (_trace.Status == "started")
            {
                _trace.Status = "failed";
            }

            _trace.CompletedAt = DateTimeOffset.UtcNow;
            _trace.DurationMs = (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        }
    }
}
