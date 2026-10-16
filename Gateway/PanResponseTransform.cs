using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Yarp.ReverseProxy.Transforms;

namespace Gateway;

public sealed class PanResponseTransform(ILogger<PanResponseTransform> logger, PrivacyTelemetry telemetry)
{
    private static readonly byte[] Failure = "{\"code\":\"privacy_inspection_failed\"}"u8.ToArray();
    private static readonly byte[] CapacityFailure = "{\"code\":\"privacy_capacity_exceeded\"}"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async ValueTask ApplyAsync(ResponseTransformContext context, CompiledPanPolicy policy)
    {
        var response = context.HttpContext.Response;
        response.Headers.CacheControl = "no-store";
        var upstream = context.ProxyResponse;
        if (upstream is null) return;
        if (HttpMethods.IsHead(context.HttpContext.Request.Method) || (int)upstream.StatusCode is 204 or 304)
        {
            context.SuppressResponseBody = true;
            return;
        }
        var content = upstream.Content;
        if (!MediaTypeHeaderValue.TryParse(content.Headers.ContentType?.ToString(), out var media)
            || !string.Equals(media.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)) return;

        // YARP has received headers but has not started the downstream response. Prevent its body copy.
        context.SuppressResponseBody = true;
        context.CancellationToken.ThrowIfCancellationRequested();
        using var inspection = telemetry.TryStartInspection(policy);
        if (inspection is null)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            logger.LogWarning(new EventId(3, "CapacityExceeded"), "Protected response blocked; inspection capacity exceeded.");
            response.Headers.Clear();
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            response.Headers.CacheControl = "no-store";
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength = CapacityFailure.Length;
            await response.Body.WriteAsync(CapacityFailure, context.CancellationToken);
            return;
        }

        var writeStarted = false;
        try
        {
            byte[] output;
            var completedOutcome = PrivacyInspectionOutcome.Inspected;
            try
            {
                if (content.Headers.ContentEncoding.Count != 0 || response.StatusCode == 206
                    || (media.CharSet is not null && !string.Equals(media.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
                    || content.Headers.ContentLength > policy.MaxBodyBytes)
                    throw new InspectionException();

                await using var source = await content.ReadAsStreamAsync(context.CancellationToken);
                using var captured = new MemoryStream();
                var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
                try
                {
                    while (true)
                    {
                        var count = await source.ReadAsync(buffer.AsMemory(0,
                            (int)Math.Min(buffer.Length, policy.MaxBodyBytes + 1L - captured.Length)), context.CancellationToken);
                        if (count == 0) break;
                        if (captured.Length + count > policy.MaxBodyBytes) throw new InspectionException();
                        captured.Write(buffer, 0, count);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }

                // JsonDocument can retain invalid UTF-8 inside unvisited strings; validate all input bytes.
                StrictUtf8.GetCharCount(captured.GetBuffer().AsSpan(0, (int)captured.Length));
                using var document = JsonDocument.Parse(captured.GetBuffer().AsMemory(0, (int)captured.Length),
                    new JsonDocumentOptions { MaxDepth = 64 });
                using var transformed = new MemoryStream();
                bool changed;
                using (var writer = new Utf8JsonWriter(transformed))
                    changed = WriteSelected(document.RootElement, writer, policy, 0);
                output = changed ? transformed.ToArray() : captured.ToArray();
                logger.LogInformation(new EventId(1, "Inspected"), "Protected response inspected; policy {PolicyId} version {Version} Mask.",
                    policy.PolicyId, policy.Version);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // Never attach exceptions: parser/transport messages may contain upstream data.
                logger.LogWarning(new EventId(2, "Blocked"), "Protected response blocked; inspection failed.");
                response.Headers.Clear();
                response.StatusCode = StatusCodes.Status502BadGateway;
                response.Headers.CacheControl = "no-store";
                output = Failure;
                completedOutcome = PrivacyInspectionOutcome.Blocked;
            }

            foreach (var header in new[] { "ETag", "Last-Modified", "Content-MD5", "Digest", "Content-Digest",
                         "Repr-Digest", "Signature", "Signature-Input", "Content-Encoding", "Content-Range", "Accept-Ranges", "Trailer", "Transfer-Encoding" })
                response.Headers.Remove(header);
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength = output.Length;
            writeStarted = true;
            await response.Body.WriteAsync(output, context.CancellationToken);
            inspection.Complete(completedOutcome);
        }
        catch (OperationCanceledException)
        {
            inspection.Complete(PrivacyInspectionOutcome.Canceled);
            throw;
        }
        catch
        {
            inspection.Complete(writeStarted
                ? PrivacyInspectionOutcome.WriteError
                : PrivacyInspectionOutcome.Faulted);
            throw;
        }
    }

    private static bool WriteSelected(JsonElement value, Utf8JsonWriter writer, CompiledPanPolicy policy, int segment)
    {
        if (segment == policy.Segments.Length)
        {
            if (value.ValueKind == JsonValueKind.Null) { value.WriteTo(writer); return false; }
            if (value.ValueKind != JsonValueKind.String) throw new InspectionException();
            writer.WriteStringValue(Mask(value.GetString()!));
            return true;
        }
        if (value.ValueKind != JsonValueKind.Object) { value.WriteTo(writer); return false; }
        var changed = false;
        writer.WriteStartObject();
        foreach (var property in value.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if (property.Name == policy.Segments[segment])
                changed |= WriteSelected(property.Value, writer, policy, segment + 1);
            else property.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
        return changed;
    }

    private static string Mask(string value)
    {
        var digits = 0;
        foreach (var c in value)
        {
            if (c is >= '0' and <= '9') digits++;
            else if (c is not (' ' or '-')) return "[REDACTED]";
        }
        if (digits <= 4) return "[REDACTED]";
        var remaining = digits - 4;
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length && remaining > 0; i++)
            if (chars[i] is >= '0' and <= '9') { chars[i] = '*'; remaining--; }
        return new string(chars);
    }

    private sealed class InspectionException : Exception;
}
