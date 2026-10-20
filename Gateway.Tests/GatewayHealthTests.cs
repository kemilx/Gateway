using System.Net;
using System.Net.Sockets;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Model;
using Xunit;

namespace Gateway.Tests;

public sealed class GatewayHealthTests
{
    private static WebApplication Build(string? upstream = null, Dictionary<string, string?>? values = null) =>
        GatewayHost.Build([], upstream, builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(values ?? PolicyConfiguration.Defaults());
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        });

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private static async Task AssertHealth(HttpClient client, string path, string status)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("{\"status\":\"" + status + "\"}"),
            await response.Content.ReadAsByteArrayAsync());
        Assert.False(response.Headers.Contains("X-Synthetic-Upstream"));
    }

    [Fact]
    public async Task Started_health_has_exact_contract_and_no_proxy_metadata()
    {
        await using var gateway = Build();
        Assert.NotNull(gateway.Services.GetRequiredService<PanPolicyCatalog>().Find("protected-payment"));
        await gateway.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(Address(gateway)), Timeout = TimeSpan.FromSeconds(5) };
        await AssertHealth(client, "/health/live", "alive");
        await AssertHealth(client, "/health/ready", "ready");
        var endpoints = gateway.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/health/", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(2, endpoints.Length);
        Assert.All(endpoints, endpoint => Assert.Null(endpoint.Metadata.GetMetadata<RouteModel>()));
        Assert.Equal(0, gateway.Services.GetRequiredService<ProxyAdmission>().ActiveRequests);
        Assert.Equal((0, 0L), gateway.Services.GetRequiredService<InspectionBudget>().Snapshot);
        await gateway.StopAsync();
    }

    [Fact]
    public async Task Readiness_tracks_started_and_stopping_tokens_on_each_request()
    {
        using var lifetime = new ManualLifetime();
        await AssertReadiness(lifetime, 503, "not_ready");
        lifetime.Started.Cancel();
        await AssertReadiness(lifetime, 200, "ready");
        lifetime.StopApplication();
        Assert.True(lifetime.ApplicationStarted.IsCancellationRequested);
        await AssertReadiness(lifetime, 503, "not_ready");
    }

    [Fact]
    public async Task Stopping_before_started_is_not_ready()
    {
        using var lifetime = new ManualLifetime();
        lifetime.StopApplication();
        await AssertReadiness(lifetime, 503, "not_ready");
    }

    private static async Task AssertReadiness(IHostApplicationLifetime lifetime, int statusCode, string status)
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await GatewayHealthEndpoints.WriteReadinessAsync(context, lifetime);
        Assert.Equal(statusCode, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("{\"status\":\"" + status + "\"}"), body.ToArray());
    }

    [Fact]
    public async Task Six_mixed_requests_waiting_for_headers_do_not_block_health_or_reserve_inspection()
    {
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
        await using var gateway = Build(Address(upstream) + "/");
        await gateway.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(Address(gateway)), Timeout = TimeSpan.FromSeconds(20) };
        var pending = Enumerable.Range(0, 6).Select(i => client.GetAsync(
            i % 2 == 0 ? "/protected/payment" : "/passthrough/payment", HttpCompletionOption.ResponseHeadersRead)).ToArray();
        try
        {
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(pending, task => Assert.False(task.IsCompleted));
            var admission = gateway.Services.GetRequiredService<ProxyAdmission>();
            var budget = gateway.Services.GetRequiredService<InspectionBudget>();
            Assert.Equal(6, admission.ActiveRequests);
            Assert.Equal((0, 0L), budget.Snapshot);
            await AssertHealth(client, "/health/live", "alive");
            await AssertHealth(client, "/health/ready", "ready");
            Assert.Equal(6, admission.ActiveRequests);
            Assert.Equal((0, 0L), budget.Snapshot);
            Assert.Equal(6, Volatile.Read(ref contacts));
            Assert.All(pending, task => Assert.False(task.IsCompleted));
        }
        finally
        {
            release.TrySetResult();
            var responses = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var response in responses) response.Dispose();
        }
        await gateway.StopAsync();
        await upstream.StopAsync();
    }

    [Fact]
    public async Task Unreachable_upstream_does_not_change_started_health()
    {
        using var unavailable = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        unavailable.Bind(new IPEndPoint(IPAddress.Loopback, 0)); // Reserved but not listening: no external service.
        var port = ((IPEndPoint)unavailable.LocalEndPoint!).Port;
        await using var gateway = Build($"http://127.0.0.1:{port}/");
        await gateway.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(Address(gateway)), Timeout = TimeSpan.FromSeconds(10) };
        using var failedProxy = await client.GetAsync("/protected/payment");
        Assert.Equal(HttpStatusCode.BadGateway, failedProxy.StatusCode);
        await AssertHealth(client, "/health/live", "alive");
        await AssertHealth(client, "/health/ready", "ready");
        await gateway.StopAsync();
    }

    [Fact]
    public void Invalid_pan_configuration_prevents_build_instead_of_serving_health()
    {
        var values = PolicyConfiguration.Defaults();
        values["PanPolicies:Policies:0:Selector"] = "$.*.cardNumber";
        var failure = Record.Exception(() => Build(values: values));
        Assert.NotNull(failure);
        Assert.True(failure is OptionsValidationException || failure.InnerException is OptionsValidationException);
    }

    private sealed class ManualLifetime : IHostApplicationLifetime, IDisposable
    {
        internal CancellationTokenSource Started { get; } = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();
        public CancellationToken ApplicationStarted => Started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;
        public void StopApplication() => stopping.Cancel();
        public void Dispose()
        {
            Started.Dispose();
            stopping.Dispose();
            stopped.Dispose();
        }
    }
}
