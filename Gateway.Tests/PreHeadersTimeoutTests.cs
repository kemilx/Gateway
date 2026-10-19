using System.Net;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Xunit;

namespace Gateway.Tests;

public sealed class PreHeadersTimeoutTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(45);
    private const string PrivateJson = "{\"payment\":{\"cardNumber\":\"4111111111111111\"}}";
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    [Trait("Category", "TransportSmoke")]
    public Task Existing_activity_timeout_before_headers_returns_bare_504_and_recovers_admission() =>
        VerifyPreHeadersTermination(cancelClient: false);

    [Fact]
    [Trait("Category", "TransportSmoke")]
    public Task Client_cancellation_before_headers_cancels_forwarding_and_recovers_admission() =>
        VerifyPreHeadersTermination(cancelClient: true);

    private static async Task VerifyPreHeadersTermination(bool cancelClient)
    {
        var entered = Signal<bool>();
        var upstreamReturned = Signal<(bool Canceled, bool HeadersStarted)>();
        var emergencyRelease = Signal();
        var forwardingEnded = Signal<ForwarderError?>();
        var releasePipelineCleanup = Signal();
        var contacts = 0;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstream = builder.Build();
        upstream.MapGet("/stall", async context =>
        {
            Interlocked.Increment(ref contacts);
            // Stage private response metadata, but deliberately never send headers or body.
            context.Response.Headers["X-Synthetic-Upstream"] = "4111111111111111";
            context.Response.ContentType = "application/json";
            entered.TrySetResult(context.Response.HasStarted);
            try { await emergencyRelease.Task.WaitAsync(context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            finally
            {
                upstreamReturned.TrySetResult((context.RequestAborted.IsCancellationRequested, context.Response.HasStarted));
            }
        });
        upstream.MapGet("/payment", async context =>
        {
            Interlocked.Increment(ref contacts);
            context.Response.Headers["X-Synthetic-Upstream"] = "recovered";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(PrivateJson, context.RequestAborted);
        });

        var values = PolicyConfiguration.Defaults();
        // Test-only capacity; the production default of six is not changed.
        values["ProxyAdmission:MaxConcurrentProxyRequests"] = "1";
        await upstream.StartAsync();
        await using var gateway = GatewayHost.Build([], Address(upstream) + "/", b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(values);
            b.WebHost.UseUrls("http://127.0.0.1:0");
        });
        // Runs inside admission, around the real YARP endpoint. Holding this finally
        // proves a canceled request cannot release its slot before pipeline cleanup.
        gateway.Use(async (context, next) =>
        {
            if (context.Request.Path != "/protected/stall")
            {
                await next(context);
                return;
            }
            try { await next(context); }
            finally
            {
                forwardingEnded.TrySetResult(context.Features.Get<IForwarderErrorFeature>()?.Error);
                await releasePipelineCleanup.Task;
            }
        });

        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1"), Timeout = Timeout.InfiniteTimeSpan };
        using var requestCancellation = new CancellationTokenSource();
        Task<HttpResponseMessage>? pending = null;
        try
        {
            await gateway.StartAsync();
            client.BaseAddress = new Uri(Address(gateway));
            var cluster = Assert.Single(gateway.Services.GetRequiredService<IProxyConfigProvider>().GetConfig().Clusters);
            Assert.Equal(TimeSpan.FromSeconds(15), cluster.HttpRequest!.ActivityTimeout);
            var admission = gateway.Services.GetRequiredService<ProxyAdmission>();
            var budget = gateway.Services.GetRequiredService<InspectionBudget>();
            Assert.Equal(0, admission.ActiveRequests);
            Assert.Equal((0, 0L), budget.Snapshot);

            pending = client.GetAsync("/protected/stall", HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token);
            Assert.False(await entered.Task.WaitAsync(Watchdog));
            Assert.False(pending.IsCompleted);
            Assert.Equal(1, admission.ActiveRequests);
            Assert.Equal((0, 0L), budget.Snapshot);
            await AssertRejected(client);
            Assert.Equal(1, Volatile.Read(ref contacts));

            if (cancelClient) requestCancellation.Cancel();

            // No sleep or tight latency assertion: the real forwarder's error feature
            // identifies which cancellation path actually terminated the stalled send.
            Assert.Equal(cancelClient ? ForwarderError.RequestCanceled : ForwarderError.RequestTimedOut,
                await forwardingEnded.Task.WaitAsync(Watchdog));
            var terminated = await upstreamReturned.Task.WaitAsync(Watchdog);
            Assert.True(terminated.Canceled);
            Assert.False(terminated.HeadersStarted);
            Assert.Equal((0, 0L), budget.Snapshot);
            Assert.Equal(1, admission.ActiveRequests);
            await AssertRejected(client);
            Assert.Equal(1, Volatile.Read(ref contacts));
            releasePipelineCleanup.TrySetResult();

            if (cancelClient)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Watchdog));
            }
            else
            {
                using var response = await pending.WaitAsync(Watchdog);
                // YARP 2.3.0 HandleRequestFailureAsync emits a bare 504 here, not
                // privacy_inspection_failed, nor the inspection deadline's Abort().
                Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
                Assert.False(response.Headers.Contains("X-Synthetic-Upstream"));
                Assert.Empty(await response.Content.ReadAsByteArrayAsync().WaitAsync(Watchdog));
            }

            await WaitForIdle(admission);
            Assert.Equal((0, 0L), budget.Snapshot);
            using var recovered = await client.GetAsync("/protected/payment").WaitAsync(Watchdog);
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            Assert.Equal("recovered", Assert.Single(recovered.Headers.GetValues("X-Synthetic-Upstream")));
            Assert.Equal("{\"payment\":{\"cardNumber\":\"************1111\"}}",
                await recovered.Content.ReadAsStringAsync().WaitAsync(Watchdog));
            Assert.Equal(2, Volatile.Read(ref contacts));
            await WaitForIdle(admission);
            Assert.Equal((0, 0L), budget.Snapshot);
        }
        finally
        {
            // Also drain the transport on assertion failure; no orphan send or
            // upstream handler is left waiting for a gate during host disposal.
            requestCancellation.Cancel();
            emergencyRelease.TrySetResult();
            releasePipelineCleanup.TrySetResult();
            try
            {
                if (pending is not null)
                {
                    try { (await pending.WaitAsync(Watchdog)).Dispose(); }
                    catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested) { }
                }
            }
            finally
            {
                try
                {
                    using var stop = new CancellationTokenSource(Watchdog);
                    await gateway.StopAsync(stop.Token);
                }
                finally
                {
                    using var stop = new CancellationTokenSource(Watchdog);
                    await upstream.StopAsync(stop.Token);
                }
            }
        }
    }

    private static async Task AssertRejected(HttpClient client)
    {
        using var rejected = await client.GetAsync("/protected/payment").WaitAsync(Watchdog);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.False(rejected.Headers.Contains("X-Synthetic-Upstream"));
        Assert.Equal("application/json; charset=utf-8", rejected.Content.Headers.ContentType!.ToString());
        Assert.True(rejected.Headers.CacheControl!.NoStore);
        Assert.Equal("{\"code\":\"gateway_capacity_exceeded\"}",
            await rejected.Content.ReadAsStringAsync().WaitAsync(Watchdog));
    }

    private static async Task WaitForIdle(ProxyAdmission admission)
    {
        using var watchdog = new CancellationTokenSource(Watchdog);
        while (admission.ActiveRequests != 0)
        {
            watchdog.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
        Assert.Equal(0, admission.ActiveRequests);
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
}
