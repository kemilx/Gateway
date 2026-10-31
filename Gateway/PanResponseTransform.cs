using System.Buffers;
using System.Collections.Immutable;
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

    public ValueTask ApplyAsync(ResponseTransformContext context, CompiledPanPolicy policy) =>
        ApplyAsync(context, policy, "unknown");

    internal async ValueTask ApplyAsync(ResponseTransformContext context, CompiledPanPolicy policy, string? serviceId)
    {
        var correlationId = context.HttpContext.Features.Get<TechnicalCorrelationCapture>()?.Id;
        var response = context.HttpContext.Response;
        response.Headers.CacheControl = "no-store";
        var upstream = context.ProxyResponse;
        if (upstream is null) return;
        if (HttpMethods.IsHead(context.HttpContext.Request.Method) || (int)upstream.StatusCode == 204)
        {
            context.SuppressResponseBody = true;
            RecordExclusion(policy, serviceId, HttpMethods.IsHead(context.HttpContext.Request.Method)
                ? "excluded_head" : "excluded_bodyless", correlationId);
            return;
        }
        if ((int)upstream.StatusCode == 304)
        {
            context.SuppressResponseBody = true;
            // Replace the upstream response before invoking any external telemetry consumer.
            response.Headers.Clear();
            response.StatusCode = StatusCodes.Status502BadGateway;
            response.Headers.CacheControl = "no-store";
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength = Failure.Length;
            context.CancellationToken.ThrowIfCancellationRequested();
            try
            {
                await response.Body.WriteAsync(Failure, context.CancellationToken);
            }
            finally
            {
                telemetry.RecordPreflightBlocked(policy,
                    summary => RecordInspectionCompleted(policy, serviceId, summary, correlationId));
            }
            return;
        }
        var content = upstream.Content;
        var parsedMedia = MediaTypeHeaderValue.TryParse(content.Headers.ContentType?.ToString(), out var media);
        var unsupported = content.Headers.ContentEncoding.Count != 0 || (int)upstream.StatusCode == 206 || response.StatusCode == 206
            || (media?.CharSet is not null && !string.Equals(media.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase));
        if (!unsupported && (!parsedMedia || !string.Equals(media?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
        {
            RecordExclusion(policy, serviceId, "excluded_media_type", correlationId);
            return;
        }

        // YARP has received headers but has not started the downstream response. Prevent its body copy.
        context.SuppressResponseBody = true;
        context.CancellationToken.ThrowIfCancellationRequested();
        using var inspection = telemetry.TryStartInspection(policy, clock,
            summary => RecordInspectionCompleted(policy, serviceId, summary, correlationId));
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
                if (unsupported
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
                        CaptureChunk(captured, buffer.AsSpan(0, count), content.Headers.ContentLength, policy.MaxBodyBytes);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }

                // JsonDocument can retain invalid UTF-8 inside unvisited strings; validate all input bytes.
                deadline.Check();
                inspection.CapturedInputBodyBytes = captured.Length;
                StrictUtf8.GetCharCount(captured.GetBuffer().AsSpan(0, (int)captured.Length));
                deadline.Check();
                using var document = JsonDocument.Parse(captured.GetBuffer().AsMemory(0, (int)captured.Length),
                    new JsonDocumentOptions { MaxDepth = 64 });
                using var transformed = new MemoryStream();
                deadline.Check();
                bool changed;
                long calculatedMasks = 0;
                long calculatedSecondaryPanMatches = 0;
                long calculatedExplicitSensitiveMatches = 0;
                // Fixed positional accounting is response-local. Public policies outside the
                // catalog contract retain legacy inspection but produce no rule events.
                var ruleCounts = HasSafeRuleMetadata(policy) ? new long[17] : null;
                using (var writer = new Utf8JsonWriter(transformed))
                    changed = policy.AdditionalRules.IsEmpty && !policy.SecondaryPanDetection
                        ? WriteSelected(document.RootElement, writer, policy, 0, deadline, ref calculatedMasks, ref calculatedExplicitSensitiveMatches)
                        : WriteMultiple(document.RootElement, writer, MultiRuleSelection.Build(policy, deadline),
                            policy.Mode, deadline, ref calculatedMasks, ref calculatedSecondaryPanMatches, ref calculatedExplicitSensitiveMatches, policy.SecondaryPanDetection, ruleCounts);
                deadline.Check();
                if (ruleCounts is not null && policy.AdditionalRules.IsEmpty && !policy.SecondaryPanDetection)
                    ruleCounts[0] = calculatedExplicitSensitiveMatches;
                var ruleSnapshot = SnapshotRules(policy, ruleCounts);
                // Commit only a fully validated traversal, never partial substitutions.
                // This measures calculation, not PAN detection or downstream delivery.
                inspection.ValidatedMasks = calculatedMasks;
                // Only secondary detector matches outside explicit rules; not all PAN or delivery.
                inspection.ValidatedSecondaryPanMatches = calculatedSecondaryPanMatches;
                // Declaration-selected strings, not proof of PAN/Email/Phone or delivery.
                inspection.ValidatedExplicitSensitiveMatches = calculatedExplicitSensitiveMatches;
                inspection.RuleMatches = ruleSnapshot;
                // A match is only a candidate block: finish validation and checkpoints first.
                if (policy.Mode == "Observe")
                {
                    // Inspection is complete; deliberately return the captured original, never the writer.
                    output = captured.ToArray();
                }
                else if (changed && policy.Mode == "Block")
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
            // Prepared body size, not bytes delivered: retain it if the write later fails.
            inspection.PreparedOutputBodyBytes = output.Length;
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

    internal static void CaptureChunk(MemoryStream captured, ReadOnlySpan<byte> chunk, long? declaredLength, int maxBodyBytes)
    {
        if (captured.Length + chunk.Length > maxBodyBytes) throw new InspectionException();
        if (chunk.IsEmpty) return;
        if (captured.Length == 0 && maxBodyBytes is > 0 and <= PanPolicyValidator.MaximumBodyBytes
            && declaredLength is > 0 && declaredLength <= maxBodyBytes)
        {
            var capacity = Math.Max(chunk.Length, (int)declaredLength.Value);
            if (captured.Capacity < capacity) captured.Capacity = capacity;
        }
        captured.Write(chunk);
    }

    internal void RecordInspectionCompleted(CompiledPanPolicy policy, string? serviceId, PrivacyTelemetry.InspectionSummary summary)
        => RecordInspectionCompleted(policy, serviceId, summary, null);

    internal void RecordInspectionCompleted(CompiledPanPolicy policy, string? serviceId,
        PrivacyTelemetry.InspectionSummary summary, string? correlationId)
    {
        if (!IsSafeIdentifier(policy.RouteId) || !IsSafeIdentifier(policy.PolicyId)
            || !IsSafeIdentifier(policy.Version)) return;
        string? correlation = correlationId;
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                var service = IsSafeIdentifier(serviceId) ? serviceId! : "unknown";
                correlation ??= Guid.NewGuid().ToString("N");
                logger.LogInformation(new EventId(5, "PrivacyInspectionCompleted"),
                    "Privacy inspection completed; schema {SchemaVersion} route {RouteId} service {ServiceId} policy {PolicyId} version {PolicyVersion} correlation {CorrelationId} outcome {Outcome} validated masks {ValidatedMasks} validated explicit sensitive matches {ValidatedExplicitSensitiveMatches} validated secondary PAN matches {ValidatedSecondaryPanMatches}.",
                    1, policy.RouteId, service, policy.PolicyId, policy.Version, correlation,
                    PrivacyTelemetry.GetOutcomeName(summary.Outcome), summary.ValidatedMasks,
                    summary.ValidatedExplicitSensitiveMatches, summary.ValidatedSecondaryPanMatches);
            }
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
        PublishRules();

        void PublishRules()
        {
            if (summary.RuleMatches.IsDefaultOrEmpty) return;
            foreach (var rule in summary.RuleMatches)
            {
                try
                {
                    if (!logger.IsEnabled(LogLevel.Information)) continue;
                    correlation ??= Guid.NewGuid().ToString("N");
                    logger.LogInformation(new EventId(6, "PrivacyRuleMatched"),
                        "Privacy rule matched; schema {SchemaVersion} route {RouteId} service {ServiceId} policy {PolicyId} version {PolicyVersion} correlation {CorrelationId} outcome {Outcome} rule {RuleId} transformation {Transformation} validated matches {ValidatedMatches} validated masks {ValidatedMasks}.",
                        1, policy.RouteId, IsSafeIdentifier(serviceId) ? serviceId! : "unknown", policy.PolicyId, policy.Version,
                        correlation, PrivacyTelemetry.GetOutcomeName(summary.Outcome), rule.RuleId, rule.Transformation,
                        rule.ValidatedMatches, rule.ValidatedMasks);
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
            }
        }
    }

    private static bool HasSafeRuleMetadata(CompiledPanPolicy policy) => policy.AdditionalRules.Length <= 15
        && IsTransformation(policy.Transformation) && policy.AdditionalRules.All(r => IsTransformation(r.Transformation));

    private static bool IsTransformation(string value) => value is "PAN" or "Email" or "Phone";

    private static ImmutableArray<PrivacyTelemetry.RuleMatch> SnapshotRules(CompiledPanPolicy policy, long[]? counts)
    {
        if (counts is null) return [];
        var snapshot = ImmutableArray.CreateBuilder<PrivacyTelemetry.RuleMatch>();
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] <= 0) continue;
            snapshot.Add(new(i == 0 ? "primary" : i == 16 ? "secondary-pan" : AdditionalRuleIds[i - 1],
                i == 0 ? policy.Transformation : i == 16 ? "PAN" : policy.AdditionalRules[i - 1].Transformation,
                counts[i], policy.Mode is "Block" or "Observe" ? 0 : counts[i]));
        }
        return snapshot.ToImmutable();
    }

    private static readonly ImmutableArray<string> AdditionalRuleIds = ["additional-0", "additional-1", "additional-2", "additional-3", "additional-4", "additional-5", "additional-6", "additional-7", "additional-8", "additional-9", "additional-10", "additional-11", "additional-12", "additional-13", "additional-14"];

    private void RecordExclusion(CompiledPanPolicy policy, string? serviceId, string reason, string? correlationId)
    {
        // Independent synchronous publications; never retry or log consumer failures.
        telemetry.RecordExclusion(policy, reason);
        try
        {
            // Even IsEnabled is external code. Prepare no event data while disabled.
            if (!logger.IsEnabled(LogLevel.Information)) return;
            // CompiledPanPolicy is publicly constructible, not proof of catalog validation.
            // Fail closed for this new event only; leave pre-existing metric/HTTP behavior intact.
            if (!IsSafeIdentifier(policy.RouteId) || !IsSafeIdentifier(policy.PolicyId)
                || !IsSafeIdentifier(policy.Version)) return;
            var service = IsSafeIdentifier(serviceId) ? serviceId! : "unknown";
            var correlation = correlationId ?? Guid.NewGuid().ToString("N");
            logger.LogInformation(new EventId(4, "PrivacyExcluded"),
                "Privacy excluded; schema {SchemaVersion} route {RouteId} service {ServiceId} policy {PolicyId} version {PolicyVersion} reason {Reason} rule evaluation {RuleEvaluation} correlation {CorrelationId}.",
                1, policy.RouteId, service, policy.PolicyId, policy.Version, reason, "not_evaluated", correlation);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
    }

    // Same grammar as PanPolicyValidator.SafeIdentifier: [A-Za-z0-9][A-Za-z0-9._-]{0,63}.
    private static bool IsSafeIdentifier(string? value) => value is { Length: > 0 and <= 64 }
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private static bool WriteSelected(JsonElement value, Utf8JsonWriter writer, CompiledPanPolicy policy, int segment,
        InspectionDeadline deadline, ref long calculatedMasks, ref long calculatedExplicitSensitiveMatches)
    {
        deadline.Check();
        if (segment == policy.Segments.Length)
        {
            if (value.ValueKind == JsonValueKind.Null) { CopyElement(value, writer, deadline); return false; }
            if (value.ValueKind != JsonValueKind.String) throw new InspectionException();
            calculatedExplicitSensitiveMatches++;
            // Block/Observe select every string by declaration; never calculate masks or stop traversal.
            if (policy.Mode is "Block" or "Observe") writer.WriteNullValue();
            else
            {
                var text = value.GetString()!;
                writer.WriteStringValue(policy.Transformation switch
                {
                    "PAN" => Mask(text),
                    "Email" => SensitiveValueMasker.Email(text),
                    "Phone" => SensitiveValueMasker.Phone(text),
                    _ => throw new InspectionException()
                });
                calculatedMasks++;
            }
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
                arrayChanged |= WriteSelected(element, writer, policy, segment + 1, deadline, ref calculatedMasks, ref calculatedExplicitSensitiveMatches);
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
                changed |= WriteSelected(property.Value, writer, policy, segment + 1, deadline, ref calculatedMasks, ref calculatedExplicitSensitiveMatches);
            else CopyElement(property.Value, writer, deadline);
        }
        writer.WriteEndObject();
        return changed;
    }

    private static bool WriteMultiple(JsonElement value, Utf8JsonWriter writer, MultiRuleSelection? selection,
        string mode, InspectionDeadline deadline, ref long calculatedMasks, ref long calculatedSecondaryPanMatches,
        ref long calculatedExplicitSensitiveMatches,
        bool secondaryPanDetection = false, long[]? ruleCounts = null)
    {
        deadline.Check();
        if (selection?.Transformation is { } transformation)
        {
            if (value.ValueKind == JsonValueKind.Null) { CopyElement(value, writer, deadline); return false; }
            if (value.ValueKind != JsonValueKind.String) throw new InspectionException();
            calculatedExplicitSensitiveMatches++;
            if (ruleCounts is not null) ruleCounts[selection.RuleIndex]++;
            if (mode is "Block" or "Observe") writer.WriteNullValue();
            else
            {
                var text = value.GetString()!;
                writer.WriteStringValue(transformation switch
                {
                    "PAN" => Mask(text),
                    "Email" => SensitiveValueMasker.Email(text),
                    "Phone" => SensitiveValueMasker.Phone(text),
                    _ => throw new InspectionException()
                });
                calculatedMasks++;
            }
            deadline.Check();
            return true;
        }
        if (secondaryPanDetection && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            var match = SecondaryPanDetector.IsMatch(text);
            deadline.Check();
            if (match)
            {
                calculatedSecondaryPanMatches++;
                if (ruleCounts is not null) ruleCounts[16]++;
                if (mode is "Block" or "Observe") writer.WriteNullValue();
                else { writer.WriteStringValue(Mask(text)); calculatedMasks++; }
                deadline.Check();
                return true;
            }
        }
        var changed = false;
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            // Enumerate every property, including duplicates; never index the document by name.
            foreach (var property in value.EnumerateObject())
            {
                deadline.Check();
                writer.WritePropertyName(property.Name);
                var child = selection?.Properties.GetValueOrDefault(property.Name);
                if (child is not null || secondaryPanDetection)
                    changed |= WriteMultiple(property.Value, writer, child, mode, deadline, ref calculatedMasks, ref calculatedSecondaryPanMatches, ref calculatedExplicitSensitiveMatches, secondaryPanDetection, ruleCounts);
                else CopyElement(property.Value, writer, deadline);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array && (selection?.ArrayItem is not null || secondaryPanDetection))
        {
            writer.WriteStartArray();
            foreach (var element in value.EnumerateArray())
            {
                deadline.Check();
                changed |= WriteMultiple(element, writer, selection?.ArrayItem, mode, deadline, ref calculatedMasks, ref calculatedSecondaryPanMatches, ref calculatedExplicitSensitiveMatches, secondaryPanDetection, ruleCounts);
            }
            writer.WriteEndArray();
        }
        else CopyElement(value, writer, deadline);
        deadline.Check();
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
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    writer.WritePropertyName(property.Name);
                    CopyElement(property.Value, writer, deadline);
                }
                else
                {
                    deadline.Check();
                    property.WriteTo(writer);
                    deadline.Check();
                }
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
