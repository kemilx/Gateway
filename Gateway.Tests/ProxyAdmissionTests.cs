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
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;
using Xunit;

namespace Gateway.Tests;

public sealed class ProxyAdmissionTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ProxyAdmission Admission(int limit = 1) =>
        new(Options.Create(new ProxyAdmissionOptions { MaxConcurrentProxyRequests = limit }));

    private static DefaultHttpContext Context(bool proxy = true)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        if (proxy)
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new RouteModel(new RouteConfig { RouteId = "synthetic" }, null!, HttpTransformer.Default)), "proxy"));
        return context;
    }

    [Fact]
    public void Atomic_capacity_and_idempotent_release_are_frozen()
    {
        var options = new ProxyAdmissionOptions { MaxConcurrentProxyRequests = 6 };
        var admission = new ProxyAdmission(Options.Create(options));
        options.MaxConcurrentProxyRequests = 1024;
        var leases = new System.Collections.Concurrent.ConcurrentBag<IDisposable>();
        Parallel.For(0, 100, _ => { if (admission.TryAcquire() is { } lease) leases.Add(lease); });
        Assert.Equal(6, leases.Count);
        Assert.Equal(6, admission.ActiveRequests);
        Assert.Null(admission.TryAcquire());
        Parallel.ForEach(leases, lease => { lease.Dispose(); lease.Dispose(); });
        Assert.Equal(0, admission.ActiveRequests);
        using var recovered = admission.TryAcquire();
        Assert.NotNull(recovered);
        Assert.Equal(1, admission.ActiveRequests);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("canceled")]
    [InlineData("exception")]
    public async Task Lease_covers_pipeline_cleanup_even_after_cancellation(string outcome)
    {
        var admission = Admission();
        var entered = Signal();
        var finishWork = Signal();
        var cleanup = Signal();
        var finishCleanup = Signal();
        using var cancellation = new CancellationTokenSource();
        var context = Context();
        context.RequestAborted = cancellation.Token;
        var failure = new IOException("Synthetic failure");
        var middleware = new ProxyAdmissionMiddleware(async _ =>
        {
            entered.SetResult();
            try
            {
                await finishWork.Task;
                if (outcome == "exception") throw failure;
                if (outcome == "canceled") throw new OperationCanceledException(cancellation.Token);
            }
            finally
            {
                cleanup.SetResult();
                await finishCleanup.Task;
            }
        });
        var pending = middleware.InvokeAsync(context, admission);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel(); // A non-cooperative pipeline still owns the permit.
        Assert.Equal(1, admission.ActiveRequests);
        Assert.Null(admission.TryAcquire());
        finishWork.SetResult();
        await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        Assert.Null(admission.TryAcquire());
        var rejected = Context();
        await middleware.InvokeAsync(rejected, admission);
        Assert.Equal(503, rejected.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", rejected.Response.ContentType);
        Assert.Equal("no-store", rejected.Response.Headers.CacheControl);
        rejected.Response.Body.Position = 0;
        Assert.Equal("{\"code\":\"gateway_capacity_exceeded\"}", await new StreamReader(rejected.Response.Body).ReadToEndAsync());
        finishCleanup.SetResult();
        if (outcome == "exception") Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => pending));
        else if (outcome == "canceled") await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        else await pending;
        Assert.Equal(0, admission.ActiveRequests);
        using var recovered = admission.TryAcquire();
        Assert.NotNull(recovered);
        Assert.Null(admission.TryAcquire());
    }

    [Fact]
    public async Task Non_proxy_endpoint_bypasses_full_admission()
    {
        var admission = Admission();
        using var occupied = admission.TryAcquire();
        var calls = 0;
        await new ProxyAdmissionMiddleware(_ => { calls++; return Task.CompletedTask; }).InvokeAsync(Context(false), admission);
        Assert.Equal(1, calls);
        Assert.Equal(1, admission.ActiveRequests);
    }

    private static WebApplication Build(Dictionary<string, string?> values, string? upstream = null) =>
        GatewayHost.Build([], upstream, b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(values);
            b.WebHost.UseUrls("http://127.0.0.1:0");
        });

    [Theory]
    [InlineData("MaxConcurrentProxyRequests", "0")]
    [InlineData("MaxConcurrentProxyRequests", "-1")]
    [InlineData("MaxConcurrentProxyRequests", "1025")]
    [InlineData("MaxConcurrentProxyRequests", "not-an-integer")]
    [InlineData("UnknownField", "1")]
    public async Task Invalid_admission_configuration_prevents_startup(string field, string value)
    {
        var values = PolicyConfiguration.Defaults();
        values["ProxyAdmission:" + field] = value;
        var failure = await Record.ExceptionAsync(async () =>
        {
            await using var app = Build(values);
            await app.StartAsync();
        });
        if (field == "MaxConcurrentProxyRequests" && value != "not-an-integer")
            Assert.IsType<OptionsValidationException>(failure);
        else Assert.IsType<InvalidOperationException>(failure);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1024)]
    public async Task Valid_admission_configuration_starts(int limit)
    {
        var values = PolicyConfiguration.Defaults();
        values["ProxyAdmission:MaxConcurrentProxyRequests"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var app = Build(values);
        await app.StartAsync();
        Assert.Equal(limit, app.Services.GetRequiredService<IOptions<ProxyAdmissionOptions>>().Value.MaxConcurrentProxyRequests);
        await app.StopAsync();
    }

    [Fact]
    public void Defaults_match_distributed_configuration()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "pan-policy-defaults.json")).Build();
        Assert.Equal(6, new ProxyAdmissionOptions().MaxConcurrentProxyRequests);
        Assert.Equal(6, configuration.GetValue<int>("ProxyAdmission:MaxConcurrentProxyRequests"));
        Assert.Equal(6, configuration.GetValue<int>("PanPolicies:InspectionBudget:MaxConcurrentInspections"));
        Assert.Equal(6291456, configuration.GetValue<int>("PanPolicies:InspectionBudget:MaxReservedBodyBytes"));
        Assert.Equal(10, configuration.GetValue<int>("PanPolicies:InspectionBudget:InspectionTimeoutSeconds"));
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    [Fact]
    public async Task Kestrel_mixed_routes_reject_before_upstream_headers_and_recover()
    {
        var allEntered = Signal();
        var release = Signal();
        var contacts = 0;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstream = builder.Build();
        upstream.MapGet("/payment", async context =>
        {
            if (Interlocked.Increment(ref contacts) == 6) allEntered.TrySetResult();
            await release.Task;
            context.Response.Headers["X-Synthetic-Upstream"] = "true";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"payment\":{\"cardNumber\":\"4111111111111111\"}}");
        });
        await upstream.StartAsync();
        await using var gateway = Build(PolicyConfiguration.Defaults(), Address(upstream) + "/");
        gateway.MapGet("/local", () => "local");
        await gateway.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(Address(gateway)), Timeout = TimeSpan.FromSeconds(20) };
        var pending = Enumerable.Range(0, 6).Select(i => client.GetAsync(i % 2 == 0 ? "/protected/payment" : "/passthrough/payment", HttpCompletionOption.ResponseHeadersRead)).ToArray();
        try
        {
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(pending, task => Assert.False(task.IsCompleted));
            Assert.Equal(6, gateway.Services.GetRequiredService<ProxyAdmission>().ActiveRequests);
            Assert.Equal((0, 0L), gateway.Services.GetRequiredService<InspectionBudget>().Snapshot);
            foreach (var route in new[] { "/protected/payment", "/passthrough/payment" })
            {
                using var rejected = await client.GetAsync(route);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
                Assert.Equal("application/json; charset=utf-8", rejected.Content.Headers.ContentType!.ToString());
                Assert.True(rejected.Headers.CacheControl!.NoStore);
                Assert.False(rejected.Headers.Contains("X-Synthetic-Upstream"));
                Assert.Equal("{\"code\":\"gateway_capacity_exceeded\"}", await rejected.Content.ReadAsStringAsync());
            }
            Assert.Equal("local", await client.GetStringAsync("/local"));
            using var missing = await client.GetAsync("/missing");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(6, Volatile.Read(ref contacts));
        }
        finally { release.TrySetResult(); }
        var responses = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < responses.Length; i++)
        {
            using var response = responses[i];
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains(i % 2 == 0 ? "************1111" : "4111111111111111", body);
        }
        // Client completion can precede the server finally; wait for observed cleanup, not a fixed delay.
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (gateway.Services.GetRequiredService<ProxyAdmission>().ActiveRequests != 0)
        {
            cleanupTimeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
        using var recovered = await client.GetAsync("/protected/payment");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(7, Volatile.Read(ref contacts));
        await gateway.StopAsync();
        await upstream.StopAsync();
    }
}
