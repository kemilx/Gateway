using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text;
using System.Threading.Channels;
using Gateway;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.LoadHarness;

internal sealed class CompareFailure(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

// Process-scoped fallback for synchronous anonymous stdin pipes. Exactly one background
// thread owns native reads. Cancellation is terminal; it does NOT promise native read
// cancellation. Never join/dispose the blocked native reader: process exit closes it.
internal sealed class CompareProcessInput : TextReader
{
    private readonly Channel<string> lines = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource lifetime = new();
    private int terminal;
    internal CompareProcessInput(Stream stdin)
    {
        var thread = new Thread(() => Pump(stdin)) { IsBackground = true, Name = "compare-stdin" };
        thread.Start();
    }
    private void Pump(Stream stdin)
    {
        try
        {
            // This reader and its stream belong exclusively to this process-lifetime pump.
            using var reader = new BufferedStream(stdin, 1024);
            var encoding = new UTF8Encoding(false, true);
            var line = new byte[16384]; var length = 0;
            while (!lifetime.IsCancellationRequested)
            {
                var c = reader.ReadByte();
                if (c < 0)
                {
                    if (length != 0) throw new CompareFailure("protocol_eof");
                    break;
                }
                if (c == '\n')
                {
                    var contentLength = length > 0 && line[length - 1] == '\r' ? length - 1 : length;
                    lines.Writer.WriteAsync(encoding.GetString(line, 0, contentLength), lifetime.Token).AsTask().GetAwaiter().GetResult();
                    length = 0;
                }
                else
                {
                    if (length == line.Length) throw new CompareFailure("protocol_invalid");
                    line[length++] = (byte)c;
                }
            }
            lines.Writer.TryComplete();
        }
        catch (Exception e) { lines.Writer.TryComplete(e is CompareFailure ? e : new CompareFailure("protocol_invalid")); }
    }
    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref terminal) != 0) throw new CompareFailure("protocol_invalid");
        try { return await lines.Reader.ReadAsync(cancellationToken); }
        catch (OperationCanceledException) { Dispose(); throw; }
        catch (ChannelClosedException e)
        {
            Dispose();
            if (e.InnerException is CompareFailure failure) throw failure;
            return null;
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref terminal, 1) == 0) lifetime.Cancel();
        // No join, no synchronous StreamReader.Dispose across a blocked Read. The worker
        // must terminate after Session fails; this object cannot restart the pump/session.
        base.Dispose(disposing);
    }
}

// Separate protocol: historical worker commands and reports are not interpreted here.
internal static class CompareProtocol
{
    internal const string Version = "gateway.rnf03.compare-worker.v1";
    internal static string Command(string kind, int? expected = null) => JsonSerializer.Serialize(new { version = Version, kind, expected }, IsolatedWorkers.Wire);
    internal static void Validate(JsonElement message, string kind)
    {
        if (!message.TryGetProperty("version", out var v) || v.GetString() != Version
            || !message.TryGetProperty("kind", out var k) || k.GetString() != kind) throw new CompareFailure("protocol_invalid");
    }
    internal static async Task<int?> Read(TextReader input, string expectedKind, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var line = await input.ReadLineAsync(timeout.Token).AsTask().WaitAsync(timeout.Token);
        if (line is null) throw new CompareFailure("protocol_eof");
        if (line.Length > 16384) throw new CompareFailure("protocol_invalid");
        try
        {
            using var document = JsonDocument.Parse(line); Validate(document.RootElement, expectedKind);
            if (expectedKind is "stop" or "ready") return null;
            var n = document.RootElement.GetProperty("expected").GetInt32();
            if (expectedKind == "begin" ? n != 0 && (n < 2 || n > 12 || n % 2 != 0) : n < 8 || n > 4096)
                throw new CompareFailure("protocol_invalid");
            return n;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new CompareFailure("protocol_invalid"); }
    }
    internal static async Task Session(TextReader input, Func<object, Task> send, Func<int, Task<(long Outcomes, long Durations)>> begin,
        Func<int, Task<CompareWindow>> end, Func<Task> stop, CancellationToken ct)
    {
        var warmups = await Read(input, "begin", ct);
        var counts = await begin(warmups!.Value);
        await send(new { version = Version, kind = "begin", outcomes = counts.Outcomes, durations = counts.Durations });
        var requests = await Read(input, "end", ct);
        var window = await end(requests!.Value);
        await send(new { version = Version, kind = "end", window });
        await Read(input, "stop", ct);
        await stop();
        await send(new { version = Version, kind = "stopped" });
    }
}

internal static class CompareWorkers
{
    internal static async Task Run(string upstream, CancellationToken ct)
    {
        IsolatedWorkers.Origin(upstream);
        using var input = new CompareProcessInput(Console.OpenStandardInput());
        await using var app = GatewayHost.Build([], upstream, b => IsolatedWorkers.Configure(b, 6));
        var budget = app.Services.GetRequiredService<InspectionBudget>();
        var admission = app.Services.GetRequiredService<ProxyAdmission>();
        long outcomes = 0, durations = 0, baseOutcomes = 0, baseDurations = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == PrivacyTelemetry.MeterName && i.Name is "gateway.privacy.inspection.outcomes" or "gateway.privacy.inspection.duration") l.EnableMeasurementEvents(i);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref outcomes, value));
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref durations));
        listener.Start();
        using var process = Process.GetCurrentProcess();
        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var peak = new ComparePeak(); Task? sampler = null;
        long started = 0, allocated = 0; double cpu = 0; int[] gcStart = []; bool mask = false; bool stopped = false;
        int[] Gcs() => [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        void Sample()
        {
            process.Refresh(); var s = budget.Snapshot;
            peak.ManagedBytes = Math.Max(peak.ManagedBytes, GC.GetTotalMemory(false));
            peak.WorkingSetBytes = Math.Max(peak.WorkingSetBytes, process.WorkingSet64);
            peak.PrivateBytes = Math.Max(peak.PrivateBytes, process.PrivateMemorySize64);
            peak.Active = Math.Max(peak.Active, s.ActiveInspections); peak.Reserved = Math.Max(peak.Reserved, s.ReservedBodyBytes);
            peak.Admission = Math.Max(peak.Admission, admission.ActiveRequests); peak.GcCounts = Gcs(); peak.Samples++;
        }
        async Task SampleLoop()
        {
            try { while (true) { Sample(); await Task.Delay(10, sampling.Token); } }
            catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
        }
        async Task Settle(long expected)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (budget.Snapshot != (0, 0L) || admission.ActiveRequests != 0
                || Interlocked.Read(ref outcomes) < expected || Interlocked.Read(ref durations) < expected)
                await Task.Delay(1, timeout.Token);
            if (Interlocked.Read(ref outcomes) != expected || Interlocked.Read(ref durations) != expected)
                throw new CompareFailure("telemetry_invalid");
        }
        async Task StopApp()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(timeout.Token); stopped = true;
        }
        Task Send(object value) => Console.Out.WriteLineAsync(JsonSerializer.Serialize(value, IsolatedWorkers.Wire));
        try
        {
            await app.StartAsync(ct);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            IsolatedWorkers.Origin(address);
            await Send(new { version = CompareProtocol.Version, kind = "ready", address });
            await CompareProtocol.Read(input, "ready", ct);
            await CompareProtocol.Session(input, Send, async expectedWarmups =>
            {
                await Settle(expectedWarmups);
                baseOutcomes = Interlocked.Read(ref outcomes); baseDurations = Interlocked.Read(ref durations); mask = expectedWarmups != 0;
                process.Refresh(); cpu = process.TotalProcessorTime.TotalMilliseconds; allocated = GC.GetTotalAllocatedBytes(true);
                gcStart = Gcs(); started = Stopwatch.GetTimestamp(); sampler = SampleLoop();
                return (baseOutcomes, baseDurations);
            }, async expected =>
            {
                await Settle(baseOutcomes + (mask ? expected : 0));
                sampling.Cancel(); if (sampler is not null) await sampler;
                Sample();
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                process.Refresh(); var cpuMs = process.TotalProcessorTime.TotalMilliseconds - cpu;
                var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                if (!double.IsFinite(elapsed) || elapsed <= 0 || !double.IsFinite(cpuMs) || cpuMs < 0 || bytes < 0)
                    throw new CompareFailure("metrics_invalid");
                return new CompareWindow(elapsed, cpuMs, bytes, Interlocked.Read(ref outcomes) - baseOutcomes,
                    Interlocked.Read(ref durations) - baseDurations, gcStart, Gcs(), peak, budget.Snapshot == (0, 0L) && admission.ActiveRequests == 0);
            }, StopApp, ct);
        }
        finally
        {
            sampling.Cancel();
            try { if (sampler is not null) await sampler; }
            finally { if (!stopped) await StopApp(); }
        }
    }
}
