using System.Net;
using System.Text;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Transforms;
using Xunit;

namespace Gateway.Tests;

[Collection("Secondary PAN telemetry isolation")]
public sealed class ProtectedCacheTests
{
    [Theory]
    [InlineData("Mask", "304")] [InlineData("Observe", "304")] [InlineData("Block", "304")]
    [InlineData("Mask", "encoding")] [InlineData("Mask", "charset")] [InlineData("Mask", "206")]
    public async Task Rejection_precedes_media_exclusion_and_capacity_without_opening_stream(string mode, string scenario)
    {
        var policy = new CompiledPanPolicy("cache-" + Guid.NewGuid().ToString("N"), "v1", "protected-payment", ["selected"], 1024) { Mode = mode };
        var budget = new InspectionBudget(Options.Create(new PanPolicyOptions { InspectionBudget = new() { MaxConcurrentInspections = 1, MaxReservedBodyBytes = 1024 } }));
        using var held = scenario == "304" ? budget.TryAcquire(1024) : null;
        if (scenario == "304") Assert.NotNull(held);
        using var telemetry = new PrivacyTelemetry(budget); var before = telemetry.DashboardMetrics;
        var logs = new TechnicalCorrelationIntegrationTests.Logs();
        using var content = new ForbiddenContent();
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        if (scenario == "encoding") content.Headers.ContentEncoding.Add("gzip");
        if (scenario == "charset") content.Headers.ContentType.CharSet = "iso-8859-1";
        using var upstream = new HttpResponseMessage(scenario == "304" ? HttpStatusCode.NotModified : scenario == "206" ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
        upstream.Headers.TryAddWithoutValidation("X-Synthetic", "body_marker");
        using var output = new MemoryStream(); var http = new DefaultHttpContext(); http.Request.Method = "GET"; http.Response.Body = output;
        http.Response.Headers["X-Synthetic"] = "body_marker";
        var context = new ResponseTransformContext { HttpContext = http, ProxyResponse = upstream };
        await new PanResponseTransform(logs, telemetry).ApplyAsync(context, policy, "synthetic-api");
        Assert.Equal(502, http.Response.StatusCode);
        Assert.Equal("{\"code\":\"privacy_inspection_failed\"}", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(output.Length, http.Response.ContentLength);
        Assert.Equal("no-store", http.Response.Headers.CacheControl);
        Assert.False(http.Response.Headers.ContainsKey("X-Synthetic"));
        Assert.Equal(0, content.ReadAttempts); Assert.Equal(scenario == "304" ? (1, 1024L) : (0, 0L), budget.Snapshot);
        Assert.Equal(before.Outcomes.Blocked + 1, telemetry.DashboardMetrics.Outcomes.Blocked);
        Assert.DoesNotContain(logs.Events, e => e.Id.Id == 4);
        Assert.Equal("blocked", Assert.Single(logs.Events, e => e.Id.Id == 5).State["Outcome"]);
        held?.Dispose(); Assert.Equal((0, 0L), budget.Snapshot);
    }

    [Fact]
    public async Task Precanceled_304_propagates_cancellation_without_opening_stream_or_acquiring_budget()
    {
        var budget = new InspectionBudget(Options.Create(new PanPolicyOptions { InspectionBudget = new() }));
        using var metrics = new OutcomeCollector("cache-precancel");
        using var telemetry = new PrivacyTelemetry(budget); using var content = new ForbiddenContent();
        var before = telemetry.DashboardMetrics;
        var logs = new TechnicalCorrelationIntegrationTests.Logs();
        using var upstream = new HttpResponseMessage(HttpStatusCode.NotModified) { Content = content };
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        using var output = new MemoryStream(); var http = new DefaultHttpContext(); http.Request.Method = "GET"; http.Response.Body = output;
        var context = new ResponseTransformContext { HttpContext = http, ProxyResponse = upstream, CancellationToken = cancel.Token };
        var policy = new CompiledPanPolicy("cache-precancel", "v1", "protected-payment", ["selected"], 1024);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PanResponseTransform(logs, telemetry).ApplyAsync(context, policy).AsTask());
        Assert.Equal(0, content.ReadAttempts); Assert.Equal((0, 0L), budget.Snapshot); Assert.Empty(output.ToArray());
        Assert.Empty(metrics.Values); Assert.Empty(logs.Events); Assert.Equal(before.Outcomes.Blocked, telemetry.DashboardMetrics.Outcomes.Blocked);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Preflight_304_records_blocked_once_even_when_error_response_write_fails_or_cancels(bool cancelWrite)
    {
        var policyId = "cache-write-" + Guid.NewGuid().ToString("N");
        var policy = new CompiledPanPolicy(policyId, "v1", "protected-payment", ["selected"], 1024);
        using var metrics = new OutcomeCollector(policyId);
        var budget = new InspectionBudget(Options.Create(new PanPolicyOptions { InspectionBudget = new() }));
        using var telemetry = new PrivacyTelemetry(budget); var before = telemetry.DashboardMetrics;
        var logs = new TechnicalCorrelationIntegrationTests.Logs();
        using var content = new ForbiddenContent();
        using var upstream = new HttpResponseMessage(HttpStatusCode.NotModified) { Content = content };
        using var cancel = new CancellationTokenSource();
        using var output = new FailingWriteStream(cancelWrite, cancel);
        var http = new DefaultHttpContext(); http.Request.Method = "GET"; http.Response.Body = output;
        var context = new ResponseTransformContext { HttpContext = http, ProxyResponse = upstream, CancellationToken = cancel.Token };
        var transform = new PanResponseTransform(logs, telemetry);
        if (cancelWrite) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transform.ApplyAsync(context, policy).AsTask());
        else await Assert.ThrowsAsync<IOException>(() => transform.ApplyAsync(context, policy).AsTask());
        Assert.Equal(1, output.WriteAttempts); Assert.Equal(0, content.ReadAttempts); Assert.Equal((0, 0L), budget.Snapshot);
        Assert.Equal(before.Outcomes.Blocked + 1, telemetry.DashboardMetrics.Outcomes.Blocked);
        var sample = Assert.Single(metrics.Values); Assert.Equal(("blocked", 1L), sample);
        Assert.DoesNotContain(logs.Events, e => e.Id.Id == 4);
        Assert.Equal("blocked", Assert.Single(logs.Events, e => e.Id.Id == 5).State["Outcome"]);
    }

    private sealed class OutcomeCollector : IDisposable
    {
        private readonly MeterListener listener = new();
        public ConcurrentQueue<(string Outcome, long Count)> Values { get; } = new();
        public OutcomeCollector(string policyId)
        {
            listener.InstrumentPublished = (instrument, subscriber) =>
            {
                if (instrument.Meter.Name == PrivacyTelemetry.MeterName && instrument.Name == "gateway.privacy.inspection.outcomes")
                    subscriber.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                string? id = null, outcome = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "gateway.policy.id") id = tag.Value as string;
                    if (tag.Key == "gateway.privacy.outcome") outcome = tag.Value as string;
                }
                if (id == policyId) Values.Enqueue((outcome!, value));
            });
            listener.Start();
        }
        public void Dispose() => listener.Dispose();
    }

    private sealed class FailingWriteStream(bool cancelWrite, CancellationTokenSource cancellation) : MemoryStream
    {
        public int WriteAttempts;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteAttempts++;
            if (!cancelWrite) return ValueTask.FromException(new IOException("synthetic_write_failure"));
            cancellation.Cancel();
            return ValueTask.FromCanceled(cancellation.Token);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class ForbiddenContent : HttpContent
    {
        public int ReadAttempts;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) { ReadAttempts++; throw new InvalidOperationException("body_marker"); }
        protected override Task<Stream> CreateContentReadStreamAsync() { ReadAttempts++; throw new InvalidOperationException("body_marker"); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public async Task Passthrough_304_and_protected_head_204_remain_bodyless()
    {
        var b = WebApplication.CreateBuilder(); b.Logging.ClearProviders(); b.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = b.Build();
        api.MapMethods("/cache/{status:int}", ["GET", "HEAD"], (HttpContext c) =>
        {
            c.Response.StatusCode = int.Parse(c.Request.RouteValues["status"]!.ToString()!);
            c.Response.Headers.ETag = "\"upstream-cache\""; return Task.CompletedTask;
        });
        await api.StartAsync();
        await using var gateway = GatewayHost.Build([], Address(api) + "/", x =>
        {
            x.Configuration.Sources.Clear(); x.Configuration.AddInMemoryCollection(PolicyConfiguration.Defaults());
            x.WebHost.UseUrls("http://127.0.0.1:0"); x.Logging.ClearProviders();
        });
        await gateway.StartAsync();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(Address(gateway)), Timeout = TimeSpan.FromSeconds(15) };
        using (var pass = await client.GetAsync("/passthrough/cache/304"))
        {
            Assert.Equal(HttpStatusCode.NotModified, pass.StatusCode); Assert.Equal("\"upstream-cache\"", pass.Headers.ETag!.ToString());
            Assert.Empty(await pass.Content.ReadAsByteArrayAsync());
        }
        foreach (var (method, status) in new[] { (HttpMethod.Head, 304), (HttpMethod.Head, 204), (HttpMethod.Get, 204) })
        {
            using var request = new HttpRequestMessage(method, "/protected/cache/" + status);
            using var response = await client.SendAsync(request);
            Assert.Equal(status, (int)response.StatusCode); Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            Assert.True(response.Headers.CacheControl!.NoStore);
        }
        Assert.Equal((0, 0L), gateway.Services.GetRequiredService<InspectionBudget>().Snapshot);
    }

    [Theory]
    [InlineData("Mask", false)] [InlineData("Mask", true)]
    [InlineData("Observe", false)] [InlineData("Observe", true)]
    [InlineData("Block", false)] [InlineData("Block", true)]
    public async Task Protected_304_is_generic_failure_without_upstream_headers_or_budget(string mode, bool conditional)
    {
        var b = WebApplication.CreateBuilder(); b.Logging.ClearProviders(); b.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = b.Build();
        api.MapGet("/cache", (Microsoft.AspNetCore.Http.HttpContext c) =>
        {
            c.Response.StatusCode = 304;
            c.Response.Headers["Set-Cookie"] = "secret=marker";
            c.Response.Headers["ETag"] = "\"marker\"";
            c.Response.Headers["Digest"] = "marker";
            c.Response.Headers["Signature"] = "marker";
            c.Response.Headers["X-Upstream-Secret"] = "marker";
            return Task.CompletedTask;
        });
        await api.StartAsync();
        var config = PolicyConfiguration.Defaults(); config["PanPolicies:Policies:0:Mode"] = mode;
        await using var gateway = GatewayHost.Build([], Address(api) + "/", x =>
        {
            x.Configuration.Sources.Clear(); x.Configuration.AddInMemoryCollection(config);
            x.WebHost.UseUrls("http://127.0.0.1:0"); x.Logging.ClearProviders();
        });
        await gateway.StartAsync();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(Address(gateway)) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/protected/cache");
        if (conditional) request.Headers.TryAddWithoutValidation("If-None-Match", "\"marker\"");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetBytes("{\"code\":\"privacy_inspection_failed\"}"), bytes);
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.True(response.Headers.CacheControl!.NoStore);
        foreach (var name in new[] { "Set-Cookie", "ETag", "Digest", "Signature", "X-Upstream-Secret" })
            Assert.DoesNotContain(response.Headers.Concat(response.Content.Headers), h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        Assert.Equal((0, 0L), gateway.Services.GetRequiredService<InspectionBudget>().Snapshot);
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
}
