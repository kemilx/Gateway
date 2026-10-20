using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Yarp.ReverseProxy.Transforms;

namespace Gateway;

public sealed class PanResponseTransform
{
    private static readonly byte[] Failure = "{\"code\":\"privacy_inspection_failed\"}"u8.ToArray();
    private static readonly byte[] PolicyBlocked = "{\"code\":\"privacy_response_blocked\"}"u8.ToArray();
    private static readonly byte[] CapacityFailure = "{\"code\":\"privacy_capacity_exceeded\"}"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ILogger<PanResponseTransform> logger;
    private readonly PrivacyTelemetry telemetry;
    private readonly TimeProvider clock;

    public PanResponseTransform(ILogger<PanResponseTransform> logger, PrivacyTelemetry telemetry, TimeProvider clock)
        : this(logger, telemetry, null, clock) { }
    private readonly Func<CancellationToken, ValueTask>? diagnosticCheckpoint;

    public PanResponseTransform(ILogger<PanResponseTransform> logger, PrivacyTelemetry telemetry)
        : this(logger, telemetry, null, TimeProvider.System)
    {
    }

    internal PanResponseTransform(ILogger<PanResponseTransform> logger, PrivacyTelemetry telemetry,
        Func<CancellationToken, ValueTask>? diagnosticCheckpoint, TimeProvider? clock = null)
    {
        this.logger = logger;
        this.telemetry = telemetry;
        this.clock = clock ?? TimeProvider.System;
        this.diagnosticCheckpoint = diagnosticCheckpoint;
    }

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
        using var inspection = telemetry.TryStartInspection(policy, clock);
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

        await using var deadline = new InspectionDeadline(clock, inspection.StartedAt, telemetry.InspectionTimeout, context.HttpContext, context.CancellationToken);
        var writeStarted = false;
        try
        {
            byte[] output;
            var completedOutcome = PrivacyInspectionOutcome.Inspected;
            var diagnosticCheckpointRunning = false;
            try
            {
                deadline.Check();
                if (content.Headers.ContentEncoding.Count != 0 || response.StatusCode == 206
                    || (media.CharSet is not null && !string.Equals(media.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
                    || content.Headers.ContentLength > policy.MaxBodyBytes)
                    throw new InspectionException();

                await using var source = await content.ReadAsStreamAsync(deadline.Token);
                using var captured = new MemoryStream();
                var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
                try
                {
                    while (true)
                    {
                        deadline.Check();
                        var count = await source.ReadAsync(buffer.AsMemory(0,
                            (int)Math.Min(buffer.Length, policy.MaxBodyBytes + 1L - captured.Length)), deadline.Token);
                        deadline.Check();
                        if (count == 0) break;
                        if (captured.Length + count > policy.MaxBodyBytes) throw new InspectionException();
                        captured.Write(buffer, 0, count);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }

                // JsonDocument can retain invalid UTF-8 inside unvisited strings; validate all input bytes.
                deadline.Check();
                StrictUtf8.GetCharCount(captured.GetBuffer().AsSpan(0, (int)captured.Length));
                deadline.Check();
                using var document = JsonDocument.Parse(captured.GetBuffer().AsMemory(0, (int)captured.Length),
                    new JsonDocumentOptions { MaxDepth = 64 });
                using var transformed = new MemoryStream();
                deadline.Check();
                bool changed;
                using (var writer = new Utf8JsonWriter(transformed))
                    changed = WriteSelected(document.RootElement, writer, policy, 0, deadline);
                deadline.Check();
                // A match is only a candidate block: finish validation and checkpoints first.
                if (changed && policy.Mode == "Block")
                {
                    output = PolicyBlocked;
                    completedOutcome = PrivacyInspectionOutcome.PolicyBlocked;
                }
                else output = changed ? transformed.ToArray() : captured.ToArray();
                deadline.Check();
                if (diagnosticCheckpoint is { } checkpoint)
                {
                    diagnosticCheckpointRunning = true;
                    await checkpoint(deadline.Token);
                    deadline.Check();
                    diagnosticCheckpointRunning = false;
                }
                logger.LogInformation(new EventId(1, "Inspected"), "Protected response inspected; policy {PolicyId} version {Version} {Mode}.",
                    policy.PolicyId, policy.Version, policy.Mode);
            }
            catch (Exception ex) when (!diagnosticCheckpointRunning
                                       && ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // Never attach exceptions: parser/transport messages may contain upstream data.
                deadline.Check();
                logger.LogWarning(new EventId(2, "Blocked"), "Protected response blocked; inspection failed.");
                deadline.PrepareResponse(() =>
                {
                    response.Headers.Clear();
                    response.StatusCode = StatusCodes.Status502BadGateway;
                    response.Headers.CacheControl = "no-store";
                });
                output = Failure;
                completedOutcome = PrivacyInspectionOutcome.Blocked;
            }

            deadline.PrepareResponse(() =>
            {
                if (completedOutcome == PrivacyInspectionOutcome.PolicyBlocked)
                {
                    response.Headers.Clear();
                    response.StatusCode = StatusCodes.Status502BadGateway;
                    response.Headers.CacheControl = "no-store";
                }
                foreach (var header in new[] { "ETag", "Last-Modified", "Content-MD5", "Digest", "Content-Digest",
                         "Repr-Digest", "Signature", "Signature-Input", "Content-Encoding", "Content-Range", "Accept-Ranges", "Trailer", "Transfer-Encoding" })
                    response.Headers.Remove(header);
                response.ContentType = "application/json; charset=utf-8";
                response.ContentLength = output.Length;
            });
            writeStarted = true;
            deadline.Check();
            await response.Body.WriteAsync(output, deadline.Token);
            deadline.Check();
            await deadline.DisposeAsync();
            inspection.Complete(deadline.TimedOut ? PrivacyInspectionOutcome.TimedOut : completedOutcome);
        }
        catch (OperationCanceledException)
        {
            await deadline.DisposeAsync();
            inspection.Complete(deadline.TimedOut ? PrivacyInspectionOutcome.TimedOut : PrivacyInspectionOutcome.Canceled);
            throw;
        }
        catch
        {
            await deadline.DisposeAsync();
            inspection.Complete(deadline.TimedOut ? PrivacyInspectionOutcome.TimedOut : deadline.ClientCanceled
                ? PrivacyInspectionOutcome.Canceled : writeStarted
                ? PrivacyInspectionOutcome.WriteError
                : PrivacyInspectionOutcome.Faulted);
            throw;
        }
    }

    private static bool WriteSelected(JsonElement value, Utf8JsonWriter writer, CompiledPanPolicy policy, int segment, InspectionDeadline deadline)
    {
        deadline.Check();
        if (segment == policy.Segments.Length)
        {
            if (value.ValueKind == JsonValueKind.Null) { CopyElement(value, writer, deadline); return false; }
            if (value.ValueKind != JsonValueKind.String) throw new InspectionException();
            // Block selects every string, including empty/non-PAN; never mask it or stop traversal.
            if (policy.Mode == "Block") writer.WriteNullValue();
            else writer.WriteStringValue(Mask(value.GetString()!));
            deadline.Check();
            return true;
        }
        if (policy.Segments[segment] == "[*]")
        {
            if (value.ValueKind != JsonValueKind.Array) { CopyElement(value, writer, deadline); return false; }
            var arrayChanged = false;
            writer.WriteStartArray();
            foreach (var element in value.EnumerateArray())
            {
                deadline.Check();
                arrayChanged |= WriteSelected(element, writer, policy, segment + 1, deadline);
            }
            writer.WriteEndArray();
            return arrayChanged;
        }
        if (value.ValueKind != JsonValueKind.Object) { CopyElement(value, writer, deadline); return false; }
        var changed = false;
        writer.WriteStartObject();
        foreach (var property in value.EnumerateObject())
        {
            deadline.Check();
            writer.WritePropertyName(property.Name);
            if (property.Name == policy.Segments[segment])
                changed |= WriteSelected(property.Value, writer, policy, segment + 1, deadline);
            else CopyElement(property.Value, writer, deadline);
        }
        writer.WriteEndObject();
        return changed;
    }

    private static void CopyElement(JsonElement value, Utf8JsonWriter writer, InspectionDeadline deadline)
    {
        deadline.Check();
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                deadline.Check();
                writer.WritePropertyName(property.Name);
                CopyElement(property.Value, writer, deadline);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var element in value.EnumerateArray()) CopyElement(element, writer, deadline);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
        deadline.Check();
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
