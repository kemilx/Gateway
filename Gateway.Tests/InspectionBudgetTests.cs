using System.Net;
using System.Text;
using Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Transforms;
using Xunit;

namespace Gateway.Tests;

public sealed class InspectionBudgetTests
{
    private const int LifecycleCycles = 10;
    private const int ReservedBodyBytes = 1048576;
    private const string JsonBody = "{\"payment\":{\"cardNumber\":\"4111111111111111\"}}";

    [Fact]
    public void Inspection_budget_CLR_defaults_match_approved_local_budget()
    {
        var options = new InspectionBudgetOptions();

        Assert.Equal(6, options.MaxConcurrentInspections);
        Assert.Equal(6L * 1024 * 1024, options.MaxReservedBodyBytes);
    }

    [Theory]
    [InlineData("missing", "")]
    [InlineData("MaxConcurrentInspections", "0")]
    [InlineData("MaxConcurrentInspections", "1025")]
    [InlineData("MaxReservedBodyBytes", "0")]
    [InlineData("MaxReservedBodyBytes", "1073741825")]
    [InlineData("MaxReservedBodyBytes", "1048575")]
    [InlineData("UnknownField", "1")]
    public void Invalid_budget_prevents_build(string field, string value)
    {
        var values = PolicyConfiguration.Defaults();
        if (field == "missing")
            foreach (var key in values.Keys.Where(k => k.Contains(":InspectionBudget:")).ToArray()) values.Remove(key);
        else values["PanPolicies:InspectionBudget:" + field] = value;
        Assert.Throws<InvalidOperationException>(() => GatewayHost.Build([], configure: b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(values);
            b.WebHost.UseUrls("http://127.0.0.1:0");
        }));
    }

    [Fact]
    public void Atomic_limits_idempotent_release_overflow_and_immutable_snapshot()
    {
        var options = new PanPolicyOptions { InspectionBudget = new() { MaxConcurrentInspections = 2, MaxReservedBodyBytes = 10 } };
        var budget = new InspectionBudget(Options.Create(options));
        options.InspectionBudget.MaxReservedBodyBytes = long.MaxValue;
        using var first = budget.TryAcquire(6);
        Assert.NotNull(first);
        Assert.Null(budget.TryAcquire(long.MaxValue));
        Assert.Null(budget.TryAcquire(5));
        var second = budget.TryAcquire(4);
        Assert.NotNull(second);
        Assert.Null(budget.TryAcquire(1));
        Parallel.For(0, 100, _ => second.Dispose());
        Assert.Equal((1, 6L), budget.Snapshot);
        first.Dispose();
        Assert.Equal((0, 0L), budget.Snapshot);
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.TryAcquire(0));
    }

    [Fact]
    public async Task Lease_is_held_until_downstream_write_completes_and_released_exactly()
    {
        for (var cycle = 0; cycle < LifecycleCycles; cycle++)
        {
            using var stream = new ControlledWriteStream();
            var (transform, context, budget, proxyResponse, telemetry) = CreateTransform(stream);
            using (proxyResponse)
            using (telemetry)
            {
                var pending = transform.ApplyAsync(context, Policy()).AsTask();

                await stream.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal((1, (long)ReservedBodyBytes), budget.Snapshot);

                stream.Complete();
                await pending.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal((0, 0L), budget.Snapshot);
                Assert.Equal(JsonBody.Replace("4111111111111111", "************1111"), stream.WrittenText);
            }
        }
    }

    [Fact]
    public async Task Lease_is_held_until_downstream_write_is_canceled_and_released_exactly()
    {
        for (var cycle = 0; cycle < LifecycleCycles; cycle++)
        {
            using var stream = new ControlledWriteStream();
            using var cancellation = new CancellationTokenSource();
            var (transform, context, budget, proxyResponse, telemetry) = CreateTransform(stream, cancellation.Token);
            using (proxyResponse)
            using (telemetry)
            {
                var pending = transform.ApplyAsync(context, Policy()).AsTask();

                await stream.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal((1, (long)ReservedBodyBytes), budget.Snapshot);

                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await pending.WaitAsync(TimeSpan.FromSeconds(5)));

                Assert.Equal((0, 0L), budget.Snapshot);
                Assert.Empty(stream.WrittenText);
            }
        }
    }

    [Fact]
    public async Task Lease_is_held_until_downstream_write_throws_and_released_exactly()
    {
        for (var cycle = 0; cycle < LifecycleCycles; cycle++)
        {
            using var stream = new ControlledWriteStream();
            var (transform, context, budget, proxyResponse, telemetry) = CreateTransform(stream);
            using (proxyResponse)
            using (telemetry)
            {
                var pending = transform.ApplyAsync(context, Policy()).AsTask();

                await stream.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal((1, (long)ReservedBodyBytes), budget.Snapshot);

                var failure = new IOException("Synthetic downstream write failure.");
                stream.Fail(failure);
                var actual = await Assert.ThrowsAsync<IOException>(async () =>
                    await pending.WaitAsync(TimeSpan.FromSeconds(5)));

                Assert.Same(failure, actual);
                Assert.Equal((0, 0L), budget.Snapshot);
                Assert.Empty(stream.WrittenText);
            }
        }
    }

    private static (PanResponseTransform Transform, ResponseTransformContext Context, InspectionBudget Budget,
        HttpResponseMessage ProxyResponse, PrivacyTelemetry Telemetry) CreateTransform(Stream downstream,
        CancellationToken cancellationToken = default)
    {
        var options = new PanPolicyOptions
        {
            InspectionBudget = new()
            {
                MaxConcurrentInspections = 1,
                MaxReservedBodyBytes = ReservedBodyBytes
            }
        };
        var budget = new InspectionBudget(Options.Create(options));
        var telemetry = new PrivacyTelemetry(budget);
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = downstream;
        var proxyResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonBody, Encoding.UTF8, "application/json")
        };
        var context = new ResponseTransformContext
        {
            HttpContext = httpContext,
            ProxyResponse = proxyResponse,
            HeadersCopied = true,
            CancellationToken = cancellationToken
        };
        return (new PanResponseTransform(NullLogger<PanResponseTransform>.Instance, telemetry), context, budget,
            proxyResponse, telemetry);
    }

    private static CompiledPanPolicy Policy() =>
        new("Default", "v1", "protected-payment", ["payment", "cardNumber"], ReservedBodyBytes);

    private sealed class ControlledWriteStream : Stream
    {
        private readonly MemoryStream written = new();
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WriteStarted => writeStarted.Task;
        public string WrittenText => Encoding.UTF8.GetString(written.ToArray());
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written.Length;
        public override long Position { get => written.Position; set => throw new NotSupportedException(); }

        public void Complete() => release.TrySetResult();
        public void Fail(Exception exception) => release.TrySetException(exception);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            writeStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            written.Write(buffer.Span);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) written.Dispose();
            base.Dispose(disposing);
        }
    }
}
