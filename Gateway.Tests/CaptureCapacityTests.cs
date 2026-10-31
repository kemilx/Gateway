using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Transforms;
using Xunit;

namespace Gateway.Tests;

public sealed class CaptureCapacityTests
{
    private const int Max = 1024 * 1024;
    private const string Body = "{\"selected\":\"4111111111111111\"}";
    private const string Failure = "{\"code\":\"privacy_inspection_failed\"}";

    [Fact]
    public void First_positive_chunk_preallocates_known_length_for_tiny_body()
    {
        using var captured = new MemoryStream();
        PanResponseTransform.CaptureChunk(captured, "{}"u8, Max, Max);
        Assert.Equal(Max, captured.Capacity);
        Assert.Equal(2, captured.Length);
        Assert.Equal("{}"u8.ToArray(), captured.ToArray());
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(1000, 1000)]
    public void First_chunk_respects_both_false_short_and_false_long_lengths(long declared, int expectedCapacity)
    {
        using var captured = new MemoryStream();
        PanResponseTransform.CaptureChunk(captured, "abcd"u8, declared, Max);
        Assert.Equal(expectedCapacity, captured.Capacity);
        Assert.Equal(4, captured.Length);
        Assert.Equal("abcd"u8.ToArray(), captured.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void Unusable_length_uses_normal_growth_without_header_preallocation(long? declared)
    {
        using var captured = new MemoryStream();
        using var normal = new MemoryStream();
        normal.Write("abc"u8);
        PanResponseTransform.CaptureChunk(captured, "abc"u8, declared, Max);
        Assert.Equal(normal.Capacity, captured.Capacity);
        Assert.Equal(3, captured.Length);
        Assert.Equal("abc"u8.ToArray(), captured.ToArray());
    }

    [Theory]
    [InlineData(PanPolicyValidator.MaximumBodyBytes + 1)]
    [InlineData(int.MaxValue)]
    public void Invalid_high_maximum_uses_normal_growth_instead_of_declared_length(int maximum)
    {
        using var captured = new MemoryStream();
        using var normal = new MemoryStream();
        normal.Write("abc"u8);
        PanResponseTransform.CaptureChunk(captured, "abc"u8, 4096, maximum);
        Assert.Equal(normal.Capacity, captured.Capacity);
        Assert.Equal(3, captured.Length);
        Assert.Equal("abc"u8.ToArray(), captured.ToArray());
    }

    [Fact]
    public void Maximum_valid_limit_allows_declared_length_preallocation()
    {
        using var captured = new MemoryStream();
        PanResponseTransform.CaptureChunk(captured, "abc"u8, 4096, PanPolicyValidator.MaximumBodyBytes);
        Assert.Equal(4096, captured.Capacity);
        Assert.Equal(3, captured.Length);
        Assert.Equal("abc"u8.ToArray(), captured.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Nonpositive_limit_rejects_positive_chunk_without_allocation_or_write(int maximum)
    {
        using var captured = new MemoryStream();
        var exception = Record.Exception(() => PanResponseTransform.CaptureChunk(captured, "a"u8, 4096, maximum));
        Assert.NotNull(exception);
        Assert.Equal("InspectionException", exception.GetType().Name);
        Assert.Equal(0, captured.Capacity);
        Assert.Equal(0, captured.Length);
        Assert.Empty(captured.ToArray());
    }

    [Fact]
    public void Zero_limit_with_empty_chunk_does_not_allocate()
    {
        using var captured = new MemoryStream();
        PanResponseTransform.CaptureChunk(captured, ReadOnlySpan<byte>.Empty, 4096, 0);
        Assert.Equal(0, captured.Capacity);
        Assert.Equal(0, captured.Length);
        Assert.Empty(captured.ToArray());
    }

    [Fact]
    public void Existing_capacity_is_not_reduced()
    {
        using var captured = new MemoryStream(4096);
        PanResponseTransform.CaptureChunk(captured, "abc"u8, 1024, Max);
        Assert.Equal(4096, captured.Capacity);
        Assert.Equal("abc"u8.ToArray(), captured.ToArray());
    }

    [Fact]
    public void Empty_chunk_does_not_preallocate_or_prevent_later_first_chunk_allocation()
    {
        using var captured = new MemoryStream();
        PanResponseTransform.CaptureChunk(captured, ReadOnlySpan<byte>.Empty, 4096, Max);
        Assert.Equal(0, captured.Capacity);
        Assert.Equal(0, captured.Length);
        PanResponseTransform.CaptureChunk(captured, "x"u8, 4096, Max);
        Assert.Equal(4096, captured.Capacity);
        Assert.Equal("x"u8.ToArray(), captured.ToArray());
    }

    [Fact]
    public void Subsequent_chunk_does_not_preallocate_from_header_again()
    {
        using var captured = new MemoryStream();
        PanResponseTransform.CaptureChunk(captured, "a"u8, null, Max);
        var capacity = captured.Capacity;
        PanResponseTransform.CaptureChunk(captured, "b"u8, Max, Max);
        Assert.Equal(capacity, captured.Capacity);
        Assert.Equal("ab"u8.ToArray(), captured.ToArray());
    }

    [Fact]
    public void Over_limit_throws_inspection_exception_without_partial_write_or_allocation()
    {
        using var captured = new MemoryStream();
        PanResponseTransform.CaptureChunk(captured, "abc"u8, null, 4);
        var capacity = captured.Capacity;
        var exception = Record.Exception(() => PanResponseTransform.CaptureChunk(captured, "de"u8, 4, 4));
        Assert.NotNull(exception);
        Assert.Equal("InspectionException", exception.GetType().Name);
        Assert.Equal(3, captured.Length);
        Assert.Equal(capacity, captured.Capacity);
        Assert.Equal("abc"u8.ToArray(), captured.ToArray());
    }

    [Theory]
    [InlineData("Mask", null)]
    [InlineData("Mask", 1L)]
    [InlineData("Mask", 1000L)]
    [InlineData("Observe", null)]
    [InlineData("Observe", 1L)]
    [InlineData("Observe", 1000L)]
    [InlineData("Block", null)]
    [InlineData("Block", 1L)]
    [InlineData("Block", 1000L)]
    public async Task Transform_reads_to_eof_despite_inaccurate_or_unknown_length(string mode, long? declared)
    {
        using var f = new Fixture(Encoding.UTF8.GetBytes(Body), declared, mode);
        await f.Run();
        var expected = mode switch { "Mask" => Body.Replace("4111111111111111", "************1111"), "Observe" => Body, _ => "{\"code\":\"privacy_response_blocked\"}" };
        Assert.Equal(expected, f.Text);
        Assert.Equal(mode == "Block" ? 502 : 200, f.Http.Response.StatusCode);
        Assert.True(f.Stream.Reads >= 2);
        Assert.Equal((0, 0L), f.Budget.Snapshot);
    }

    [Fact]
    public async Task Empty_body_fails_closed_and_releases_lease()
    {
        using var f = new Fixture([], Max);
        await f.Run();
        Assert.Equal(502, f.Http.Response.StatusCode);
        Assert.Equal(Failure, f.Text);
        Assert.Equal((0, 0L), f.Budget.Snapshot);
    }

    [Fact]
    public async Task First_read_failure_fails_closed_without_partial_pii()
    {
        using var f = new Fixture(Encoding.UTF8.GetBytes(Body), Max);
        f.Stream.Fail = true;
        await f.Run();
        Assert.Equal(502, f.Http.Response.StatusCode);
        Assert.Equal(Failure, f.Text);
        Assert.Equal(1, f.Stream.Reads);
        Assert.Equal((0, 0L), f.Budget.Snapshot);
    }

    [Theory]
    [InlineData(Max + 1L)]
    [InlineData(long.MaxValue)]
    public async Task Oversized_declared_length_is_rejected_before_stream_open(long declared)
    {
        using var f = new Fixture(Encoding.UTF8.GetBytes(Body), declared);
        await f.Run();
        Assert.Equal(502, f.Http.Response.StatusCode);
        Assert.Equal(Failure, f.Text);
        Assert.Equal(0, f.Content.Opens);
        Assert.Equal(0, f.Stream.Reads);
        Assert.Equal((0, 0L), f.Budget.Snapshot);
    }

    [Fact]
    public async Task Denied_lease_never_opens_or_reads_content()
    {
        using var f = new Fixture(Encoding.UTF8.GetBytes(Body), Max);
        using var held = f.Budget.TryAcquire(Max);
        Assert.NotNull(held);
        await f.Run();
        Assert.Equal(503, f.Http.Response.StatusCode);
        Assert.Equal("{\"code\":\"privacy_capacity_exceeded\"}", f.Text);
        Assert.Equal(0, f.Content.Opens);
        Assert.Equal(0, f.Stream.Reads);
        held.Dispose();
        Assert.Equal((0, 0L), f.Budget.Snapshot);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, 1L)]
    [InlineData(true, 1L)]
    public async Task Actual_size_boundary_is_enforced_independently_of_declared_length(bool over, long? declared)
    {
        var bytes = Encoding.UTF8.GetBytes("{\"x\":\"4111111111111111" + new string('a', Max - 24 + (over ? 1 : 0)) + "\"}");
        Assert.Equal(Max + (over ? 1 : 0), bytes.Length);
        using var f = new Fixture(bytes, declared);
        await f.Run();
        Assert.Equal(over ? 502 : 200, f.Http.Response.StatusCode);
        Assert.Equal(over ? Failure : Encoding.UTF8.GetString(bytes), f.Text);
        Assert.Equal((0, 0L), f.Budget.Snapshot);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("during")]
    [InlineData("after")]
    public async Task Cancellation_before_during_or_after_read_releases_lease_without_output(string when)
    {
        using var cancel = new CancellationTokenSource();
        using var f = new Fixture(Encoding.UTF8.GetBytes(Body), Max, token: cancel.Token);
        if (when == "before") cancel.Cancel();
        else { f.Stream.Cancel = cancel; f.Stream.CancelAfterRead = when == "after"; }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run());
        Assert.Empty(f.Text);
        Assert.Equal((0, 0L), f.Budget.Snapshot);
        if (when == "before") { Assert.Equal(0, f.Content.Opens); Assert.Equal(0, f.Stream.Reads); }
        else Assert.Equal(1, f.Stream.Reads);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly InspectionBudget Budget;
        public readonly PrivacyTelemetry Telemetry;
        public readonly ProbeStream Stream;
        public readonly ProbeContent Content;
        public readonly DefaultHttpContext Http = new();
        private readonly MemoryStream output = new();
        private readonly HttpResponseMessage upstream;
        private readonly ResponseTransformContext context;
        private readonly CompiledPanPolicy policy;
        public string Text => Encoding.UTF8.GetString(output.ToArray());

        public Fixture(byte[] bytes, long? declared, string mode = "Mask", CancellationToken token = default)
        {
            Budget = new InspectionBudget(Options.Create(new PanPolicyOptions { InspectionBudget = new() { MaxConcurrentInspections = 1, MaxReservedBodyBytes = Max } }));
            Telemetry = new PrivacyTelemetry(Budget);
            Stream = new ProbeStream(bytes);
            Content = new ProbeContent(Stream);
            Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            Content.Headers.ContentLength = declared;
            upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = Content };
            Http.Response.Body = output;
            context = new ResponseTransformContext { HttpContext = Http, ProxyResponse = upstream, CancellationToken = token };
            policy = new CompiledPanPolicy("capture", "v1", "protected-payment", ["selected"], Max) { Mode = mode };
        }

        public Task Run() => new PanResponseTransform(NullLogger<PanResponseTransform>.Instance, Telemetry).ApplyAsync(context, policy).AsTask();
        public void Dispose() { upstream.Dispose(); Telemetry.Dispose(); output.Dispose(); Stream.Dispose(); }
    }

    private sealed class ProbeContent(ProbeStream stream) : HttpContent
    {
        public int Opens;
        protected override Task<Stream> CreateContentReadStreamAsync() { Opens++; return Task.FromResult<Stream>(stream); }
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateContentReadStreamAsync();
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class ProbeStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int Reads;
        public bool Fail;
        public CancellationTokenSource? Cancel;
        public bool CancelAfterRead;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (Fail) return ValueTask.FromException<int>(new IOException("synthetic failure"));
            if (Cancel is not null && !CancelAfterRead) { Cancel.Cancel(); return ValueTask.FromCanceled<int>(Cancel.Token); }
            var count = base.Read(buffer.Span);
            Cancel?.Cancel();
            return ValueTask.FromResult(count);
        }
    }
}
