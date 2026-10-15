using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Yarp.ReverseProxy.Transforms;

namespace Gateway;

public sealed class PanResponseTransform(ILogger<PanResponseTransform> logger)
{
    public const int MaxBodyBytes = 1024 * 1024;
    private static readonly byte[] Failure = "{\"code\":\"privacy_inspection_failed\"}"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async ValueTask ApplyAsync(ResponseTransformContext context)
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
        byte[] output;
        try
        {
            if (content.Headers.ContentEncoding.Count != 0 || response.StatusCode == 206
                || (media.CharSet is not null && !string.Equals(media.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
                || content.Headers.ContentLength > MaxBodyBytes)
                throw new InspectionException();

            await using var source = await content.ReadAsStreamAsync(context.CancellationToken);
            using var captured = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                while (true)
                {
                    var count = await source.ReadAsync(buffer.AsMemory(0,
                        (int)Math.Min(buffer.Length, MaxBodyBytes + 1L - captured.Length)), context.CancellationToken);
                    if (count == 0) break;
                    if (captured.Length + count > MaxBodyBytes) throw new InspectionException();
                    captured.Write(buffer, 0, count);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }

            // JsonDocument can retain invalid UTF-8 inside unvisited strings; validate all input bytes.
            StrictUtf8.GetCharCount(captured.GetBuffer().AsSpan(0, (int)captured.Length));
            using var document = JsonDocument.Parse(captured.GetBuffer().AsMemory(0, (int)captured.Length),
                new JsonDocumentOptions { MaxDepth = 64 });
            using var transformed = new MemoryStream();
            using (var writer = new Utf8JsonWriter(transformed))
                WriteRoot(document.RootElement, writer);
            output = transformed.ToArray();
            logger.LogInformation(new EventId(1, "Inspected"), "Protected payment response inspected; policy PAN-v1 Mask.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // Never attach exceptions: parser/transport messages may contain upstream data.
            logger.LogWarning(new EventId(2, "Blocked"), "Protected payment response blocked; inspection failed.");
            response.Headers.Clear();
            response.StatusCode = StatusCodes.Status502BadGateway;
            response.Headers.CacheControl = "no-store";
            output = Failure;
        }

        foreach (var header in new[] { "ETag", "Last-Modified", "Content-MD5", "Digest", "Content-Digest",
                     "Repr-Digest", "Signature", "Signature-Input", "Content-Encoding", "Content-Range", "Accept-Ranges", "Trailer", "Transfer-Encoding" })
            response.Headers.Remove(header);
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength = output.Length;
        await response.Body.WriteAsync(output, context.CancellationToken);
    }

    private static void WriteRoot(JsonElement root, Utf8JsonWriter writer)
    {
        if (root.ValueKind != JsonValueKind.Object) { root.WriteTo(writer); return; }
        writer.WriteStartObject();
        foreach (var property in root.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if (property.Name != "payment" || property.Value.ValueKind != JsonValueKind.Object)
            { property.Value.WriteTo(writer); continue; }
            writer.WriteStartObject();
            foreach (var field in property.Value.EnumerateObject())
            {
                writer.WritePropertyName(field.Name);
                if (field.Name != "cardNumber" || field.Value.ValueKind == JsonValueKind.Null)
                    field.Value.WriteTo(writer);
                else if (field.Value.ValueKind == JsonValueKind.String)
                    writer.WriteStringValue(Mask(field.Value.GetString()!));
                else throw new InspectionException();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
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
