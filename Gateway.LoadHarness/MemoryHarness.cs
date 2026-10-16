using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Runtime;
using System.Text;
using System.Text.Json;
using Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Transforms;

namespace Gateway.LoadHarness;

internal static class MemoryHarness
{
    private static readonly CompiledPanPolicy Policy = new("PAN", "v1", "protected-payment", ["payment", "cardNumber"], Common.BodySize);
    internal static async Task<WorkerReport> Worker(int concurrency, CancellationToken ct)
    {
        var payload = Common.Payload();
        var budget = new InspectionBudget(Options.Create(new PanPolicyOptions { InspectionBudget = new() }));
        using var telemetry = new PrivacyTelemetry(budget);
        var checkpoint = new Barrier(concurrency); var writes = new Barrier(concurrency);
        var transform = new PanResponseTransform(NullLogger<PanResponseTransform>.Instance, telemetry, checkpoint.Arrive);
        var warm = new PanResponseTransform(NullLogger<PanResponseTransform>.Instance, telemetry);
        await Invoke(warm, "{\"payment\":{\"cardNumber\":\"4111111111111111\"}}"u8.ToArray(), Stream.Null, ct);
        await Invoke(warm, payload, Stream.Null, ct);
        long outcomes = 0;
        long durations = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PrivacyTelemetry.MeterName
                && instrument.Name is "gateway.privacy.inspection.outcomes" or "gateway.privacy.inspection.duration")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref outcomes, value));
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref durations));
        listener.Start();
        Common.Collect(true);
        var samples = new List<Sample>(6) { Common.Measure("baseline", true, budget) };
        var saturation = new Saturation(false, null, null, null);
        var streams = Enumerable.Range(0, concurrency).Select(_ => new ControlledWriteStream(writes)).ToArray();
        var tasks = streams.Select(stream => Invoke(transform, payload, stream, ct)).ToArray();
        try
        {
            await checkpoint.Ready.Task.WaitAsync(ct);
            Capture("output-ready", concurrency, budget, samples);
            Common.Require(Interlocked.Read(ref outcomes) == 0);
            checkpoint.Release.TrySetResult();
            await writes.Ready.Task.WaitAsync(ct);
            Capture("write-blocked", concurrency, budget, samples);
            Common.Require(Interlocked.Read(ref outcomes) == 0);
            if (concurrency == 8)
            {
                using var origin = new CountingContent();
                using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = origin };
                var http = new DefaultHttpContext(); http.Response.Body = Stream.Null;
                await transform.ApplyAsync(new ResponseTransformContext { HttpContext = http, ProxyResponse = response, CancellationToken = ct }, Policy);
                var unchanged = budget.Snapshot == (8, 8L * Common.BodySize);
                saturation = new(true, http.Response.StatusCode, origin.Reads, unchanged);
                Common.Require(http.Response.StatusCode == 503 && origin.Reads == 0 && unchanged);
            }
            writes.Release.TrySetResult(); await Task.WhenAll(tasks).WaitAsync(ct);
            Common.Require(Interlocked.Read(ref outcomes) == concurrency + (concurrency == 8 ? 1 : 0));
            Common.Require(Interlocked.Read(ref durations) == concurrency);
            Common.Require(budget.Snapshot == (0, 0L) && streams.All(s => s.Canary));
        }
        finally
        {
            checkpoint.Release.TrySetResult(); writes.Release.TrySetResult();
            await Task.WhenAll(tasks).WaitAsync(ct);
            foreach (var stream in streams) stream.Dispose();
        }
        tasks = []; streams = []; telemetry.Dispose(); listener.Dispose(); Common.Collect();
        samples.Add(Common.Measure("post-release", true, budget));
        GC.KeepAlive(payload);
        return new("gateway.mvp05.memory-worker.v1", Common.Revision, Environment.Version.ToString(),
            OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Other",
            Environment.ProcessorCount, GCSettings.IsServerGC, GCSettings.LatencyMode.ToString(), "token-dense-v1",
            Common.BodySize, concurrency, 8, 8L * Common.BodySize, samples, saturation,
            Interlocked.Read(ref outcomes), Interlocked.Read(ref durations), true, true, "passed");
    }
    private static void Capture(string phase, int n, InspectionBudget budget, List<Sample> samples)
    {
        Common.Require(budget.Snapshot == (n, n * (long)Common.BodySize));
        samples.Add(Common.Measure(phase, false, budget)); Common.Collect(); samples.Add(Common.Measure(phase, true, budget));
        Common.Require(budget.Snapshot == (n, n * (long)Common.BodySize));
    }
    private static async Task Invoke(PanResponseTransform transform, byte[] bytes, Stream output, CancellationToken ct)
    {
        using var content = new StreamContent(new MemoryStream(bytes, false));
        content.Headers.ContentType = new("application/json"); content.Headers.ContentLength = bytes.Length;
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        var http = new DefaultHttpContext(); http.Response.Body = output;
        await transform.ApplyAsync(new ResponseTransformContext { HttpContext = http, ProxyResponse = response, CancellationToken = ct }, Policy);
        Common.Require(http.Response.StatusCode == 200);
    }
    internal static async Task Run(string output, CancellationToken ct)
    {
        var workers = new List<WorkerReport>(Common.Concurrencies.Length);
        foreach (var n in Common.Concurrencies)
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(typeof(MemoryHarness).Assembly.Location); start.ArgumentList.Add("memory-worker"); start.ArgumentList.Add("--concurrency"); start.ArgumentList.Add(n.ToString());
            using var process = Process.Start(start) ?? throw new InvalidOperationException();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token); var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            var text = await stdout; await stderr;
            Common.Require(process.ExitCode == 0);
            var worker = JsonSerializer.Deserialize<WorkerReport>(text, Common.Json) ?? throw new InvalidOperationException();
            Common.Require(worker.Schema == "gateway.mvp05.memory-worker.v1" && worker.Status == "passed" && worker.Concurrency == n && worker.Release && worker.Canary);
            workers.Add(worker);
        }
        var report = new MemoryReport("gateway.mvp05.memory.v1", "passed", "MEASURED_NOT_SIZED", "No target memory limit. Heap information reflects the latest GC; natural samples do not force GC. Source payload is excluded from baseline deltas.", workers,
            workers.SelectMany(w => w.Samples.Skip(1).Select(s => new Delta(w.Concurrency, s.Phase, s.ForcedGc,
                s.ManagedBytes - w.Samples[0].ManagedBytes, s.WorkingSetBytes - w.Samples[0].WorkingSetBytes, s.PrivateBytes - w.Samples[0].PrivateBytes))).ToArray());
        await Common.Save(output, "memory", report, Markdown);
    }
    private static string Markdown(MemoryReport r)
    {
        var text = new StringBuilder($"# Memory: {r.Status}\n\n{r.Classification}. {r.Notes}\n\n|Concurrency|Phase|Forced GC|Managed delta bytes|Working set delta bytes|Private delta bytes|\n|---:|---|---|---:|---:|---:|\n");
        foreach (var d in r.Deltas) text.AppendLine($"|{d.Concurrency}|{d.Phase}|{d.ForcedGc}|{d.ManagedBytes}|{d.WorkingSetBytes}|{d.PrivateBytes}|");
        return text.ToString();
    }
}
internal sealed class Barrier(int count)
{
    private int arrived;
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal async ValueTask Arrive(CancellationToken ct) { if (Interlocked.Increment(ref arrived) == count) Ready.TrySetResult(); await Release.Task.WaitAsync(ct); }
}
internal sealed class ControlledWriteStream(Barrier barrier) : Stream
{
    internal bool Canary { get; private set; }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { await barrier.Arrive(cancellationToken); Canary = Common.Canary(buffer.Span, Common.BodySize); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
internal sealed class CountingContent : HttpContent
{
    internal int Reads;
    internal CountingContent() { Headers.ContentType = new("application/json"); Headers.ContentLength = Common.BodySize; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) { Interlocked.Increment(ref Reads); throw new InvalidOperationException(); }
    protected override Task<Stream> CreateContentReadStreamAsync() { Interlocked.Increment(ref Reads); throw new InvalidOperationException(); }
    protected override bool TryComputeLength(out long length) { length = Common.BodySize; return true; }
}
internal sealed record Saturation(bool Tested, int? StatusCode, int? OriginReads, bool? BudgetUnchanged);
internal sealed record WorkerReport(string Schema, string BaseRevision, string Runtime, string Os, int ProcessorCount, bool ServerGc,
    string LatencyMode, string Profile, int BodyBytes, int Concurrency, int MaxConcurrent, long MaxReserved,
    List<Sample> Samples, Saturation Saturation, long OutcomeCount, long DurationCount, bool Release, bool Canary,
    string Status);
internal sealed record Delta(int Concurrency, string Phase, bool ForcedGc, long ManagedBytes, long WorkingSetBytes, long PrivateBytes);
internal sealed record MemoryReport(string Schema, string Status, string Classification, string Notes, List<WorkerReport> Workers, Delta[] Deltas);
