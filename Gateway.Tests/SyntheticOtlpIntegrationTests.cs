using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TestApi;
using Xunit;

namespace Gateway.Tests;

public sealed class SyntheticOtlpIntegrationTests
{
    [Fact]
    public async Task Unavailable_collector_does_not_change_gateway_http_behavior()
    {
        using var reservation = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var endpoint = $"http://127.0.0.1:{port}/";
        var api = SyntheticApi.Build([], builder => builder.WebHost.UseUrls("http://127.0.0.1:0"));
        await api.StartAsync();
        var config = PolicyConfiguration.Defaults();
        config["SyntheticOtlp:Enabled"] = "true";
        config["SyntheticOtlp:Endpoint"] = endpoint;
        var gateway = GatewayHost.Build([], Address(api) + "/", builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(config);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        });
        await gateway.StartAsync();
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(Address(gateway)) };
            using var response = await client.GetAsync("/protected/payment");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(SyntheticApi.Pan, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await gateway.DisposeAsync();
            await api.DisposeAsync();
        }
    }

    [Fact]
    public async Task Real_otlp_receiver_observes_metrics_and_correlated_events_from_both_hosts()
    {
        var received = new ConcurrentQueue<(string Path, byte[] Body, string? ContentType)>();
        var collector = WebApplication.CreateBuilder();
        collector.WebHost.UseUrls("http://127.0.0.1:0");
        var collectorApp = collector.Build();
        collectorApp.MapPost("/{**path}", async context =>
        {
            using var stream = new MemoryStream();
            await context.Request.Body.CopyToAsync(stream);
            received.Enqueue((context.Request.Path, stream.ToArray(), context.Request.ContentType));
            context.Response.StatusCode = 200;
        });
        await collectorApp.StartAsync();
        var collectorOrigin = Address(collectorApp) + "/";

        var api = SyntheticApi.Build([], builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TechnicalCorrelation:Enabled"] = "true",
                ["SyntheticOtlp:Enabled"] = "true",
                ["SyntheticOtlp:Endpoint"] = collectorOrigin
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        });
        await api.StartAsync();
        var gatewayConfig = PolicyConfiguration.Defaults();
        gatewayConfig["TechnicalCorrelation:Enabled"] = "true";
        gatewayConfig["SyntheticOtlp:Enabled"] = "true";
        gatewayConfig["SyntheticOtlp:Endpoint"] = collectorOrigin;
        var gateway = GatewayHost.Build([], Address(api) + "/", builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(gatewayConfig);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        });
        await gateway.StartAsync();

        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(Address(gateway)) };
            using var response = await client.GetAsync("/protected/payment");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (!Has("/v1/metrics", "gateway.privacy.inspection.outcomes")
                   || !Has("/v1/logs", "PrivacyInspectionCompleted")
                   || !Has("/v1/logs", "TechnicalCorrelationObserved"))
                await Task.Delay(50, timeout.Token);
            Assert.All(received, item => Assert.Equal("application/x-protobuf", item.ContentType));
            Assert.DoesNotContain(received, item => Encoding.UTF8.GetString(item.Body).Contains(SyntheticApi.Pan, StringComparison.Ordinal));
        }
        finally
        {
            await gateway.DisposeAsync();
            await api.DisposeAsync();
            await collectorApp.DisposeAsync();
        }

        bool Has(string path, string marker) => received.Any(item => item.Path == path
            && Encoding.UTF8.GetString(item.Body).Contains(marker, StringComparison.Ordinal));
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
}
