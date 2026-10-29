using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using MaiImageGenerator.Shared.Data.Entities;
using OpenAI;
using OpenAI.Chat;

namespace MaiImageGenerator.Shared.Generation;

#pragma warning disable OPENAI001 // Keep the delegate's model and endpoint while capturing raw, nullable usage.
internal sealed class FoundryChatTraceClient(ChatClient inner, AiGenerationTrace trace)
    : ChatClient(inner.Pipeline, inner.Model, new OpenAIClientOptions { Endpoint = inner.Endpoint })
#pragma warning restore OPENAI001
{
    public override async Task<ClientResult> CompleteChatAsync(BinaryContent content, RequestOptions? options = null)
    {
        ClientResult result;
        try
        {
            result = await inner.CompleteChatAsync(content, options);
        }
        catch (ClientResultException exception)
        {
            if (exception.GetRawResponse() is { } response)
            {
                AiTraceCapture.CaptureRequestId(trace, name =>
                    response.Headers.TryGetValue(name, out var value) ? value : null);
                AiTraceCapture.CaptureChatResponse(trace, () => JsonDocument.Parse(response.Content.ToMemory()));
            }

            throw;
        }

        AiTraceCapture.CaptureRequestId(trace, name =>
            result.GetRawResponse().Headers.TryGetValue(name, out var value) ? value : null);
        AiTraceCapture.CaptureChatResponse(trace, () =>
            JsonDocument.Parse(result.GetRawResponse().Content.ToMemory()));
        using var document = JsonDocument.Parse(result.GetRawResponse().Content.ToMemory());
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("usage", out var usage) ||
            IsSupportedUsage(usage, trace))
        {
            return result;
        }

        // The SDK uses nonnullable Int32 counters. Preserve raw nullable Int64 counts in the trace and
        // give its response parser only supported counter values, never malformed optional metadata.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name != "usage")
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteStartObject("usage");
            WriteCount(writer, "prompt_tokens", trace.InputTokens);
            WriteCount(writer, "completion_tokens", trace.OutputTokens);
            WriteCount(writer, "total_tokens", trace.TotalTokens);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return ClientResult.FromResponse(new UsageSafeResponse(
            result.GetRawResponse(), BinaryData.FromBytes(stream.ToArray())));
    }

    private static bool IsSupportedUsage(JsonElement usage, AiGenerationTrace trace)
    {
        try
        {
            if (usage.ValueKind != JsonValueKind.Null)
            {
                _ = ModelReaderWriter.Read<ChatTokenUsage>(BinaryData.FromString(usage.GetRawText()));
            }

            return true;
        }
        catch (Exception)
        {
            AiTraceCapture.ReportMetadataFailure(trace);
            return false;
        }
    }

    private static void WriteCount(Utf8JsonWriter writer, string name, long? count)
    {
        if (count is >= 0 and <= int.MaxValue)
        {
            writer.WriteNumber(name, count.Value);
        }
    }

    private sealed class UsageSafeResponse(PipelineResponse inner, BinaryData content) : PipelineResponse
    {
        public override int Status => inner.Status;
        public override string ReasonPhrase => inner.ReasonPhrase;
        protected override PipelineResponseHeaders HeadersCore => inner.Headers;
        public override Stream? ContentStream { get; set; } = content.ToStream();
        public override BinaryData Content => content;
        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => content;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(content);
        protected override bool IsErrorCore { get => inner.IsError; set { } }
        public override void Dispose()
        {
            ContentStream?.Dispose();
            inner.Dispose();
        }
    }
}
