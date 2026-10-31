using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyntheticTelemetry;
using TestApi;
using Xunit;

namespace Gateway.Tests;

public sealed class SyntheticOtlpResilienceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private const string Template = "Synthetic request received; correlation {CorrelationId}.";

    [Fact]
    public async Task Redirect_is_not_followed_and_sink_positive_control_is_reachable()
    {
        await using var sink = await Receiver.Start(_ => Task.FromResult(200));
        await using var source = await Receiver.Start(context =>
        {
            context.Response.Headers.Location = sink.Origin + "sink";
            return Task.FromResult(302);
        });
        using (var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }))
        {
            using var response = await client.GetAsync(sink.Origin + "sink");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(("GET", "/sink"), Assert.Single(sink.Requests));
        using (var provider = Provider(source))
        {
            Emit(provider, 1);
            await source.Reached.Task.WaitAsync(Deadline);
        }
        Assert.Equal(("GET", "/sink"), Assert.Single(sink.Requests));
        Assert.Contains(source.Bodies, body => body.Contains(Id(1), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Collector_500_then_success_accepts_later_log()
    {
        var count = 0;
        await using var receiver = await Receiver.Start(_ => Task.FromResult(Interlocked.Increment(ref count) == 1 ? 500 : 200));
        using var provider = Provider(receiver);
        Emit(provider, 1);
        await receiver.Reached.Task.WaitAsync(Deadline);
        Emit(provider, 2);
        await WaitUntil(() => receiver.Bodies.Any(body => body.Contains(Id(2), StringComparison.Ordinal)));
        Assert.True(Volatile.Read(ref count) >= 2);
    }

    [Theory]
    [InlineData("/v1/logs")]
    [InlineData("/v1/metrics")]
    public async Task Held_collector_does_not_prevent_readiness_masking_or_passthrough(string signalPath)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var receiver = await Receiver.Start(async context =>
        {
            if (context.Request.Path != signalPath) return 200;
            using var registration = context.RequestAborted.Register(() => canceled.TrySetResult());
            held.TrySetResult();
            await release.Task.WaitAsync(context.RequestAborted);
            return 200;
        }, signalPath);
        WebApplication? api = null, gateway = null;
        try
        {
            api = SyntheticApi.Build([], b => b.WebHost.UseUrls("http://127.0.0.1:0"));
            await api.StartAsync();
            var config = PolicyConfiguration.Defaults();
            config["SyntheticOtlp:Enabled"] = "true";
            config["SyntheticOtlp:Endpoint"] = receiver.Origin;
            gateway = GatewayHost.Build([], Address(api) + "/", b =>
            {
                b.Configuration.Sources.Clear(); b.Configuration.AddInMemoryCollection(config);
                b.WebHost.UseUrls("http://127.0.0.1:0");
            });
            await gateway.StartAsync();
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(Address(gateway)), Timeout = Deadline };
            using (var initial = await client.GetAsync("/protected/payment")) Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
            await receiver.Reached.Task.WaitAsync(Deadline);
            await held.Task.WaitAsync(Deadline);
            Assert.Contains(receiver.Requests, request => request.Path == signalPath);
            Assert.False(release.Task.IsCompleted);
            Assert.False(canceled.Task.IsCompleted);
            var requests = new[] { client.GetAsync("/health/ready"), client.GetAsync("/protected/payment"), client.GetAsync("/passthrough/payment") };
            var responses = await Task.WhenAll(requests).WaitAsync(Deadline);
            try
            {
                Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
                Assert.DoesNotContain(SyntheticApi.Pan, await responses[1].Content.ReadAsStringAsync());
                Assert.Equal(SyntheticApi.Sample, await responses[2].Content.ReadAsStringAsync());
                Assert.False(release.Task.IsCompleted);
                Assert.False(canceled.Task.IsCompleted);
            }
            finally { foreach (var response in responses) response.Dispose(); }
        }
        finally
        {
            release.TrySetResult();
            try { if (gateway is not null) await gateway.DisposeAsync().AsTask().WaitAsync(Deadline); }
            finally { if (api is not null) await api.DisposeAsync().AsTask().WaitAsync(Deadline); }
        }
    }

    [Fact]
    public async Task Blocked_export_saturates_real_batch_queue_without_blocking_producer()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var receiver = await Receiver.Start(async context =>
        {
            using var registration = context.RequestAborted.Register(() => canceled.TrySetResult());
            await release.Task.WaitAsync(context.RequestAborted);
            return 200;
        });
        var provider = Provider(receiver);
        try
        {
            for (var i = 1; i <= 64; i++) Emit(provider, i);
            await receiver.Reached.Task.WaitAsync(Deadline);
            var producer = Task.Run(() => { for (var i = 65; i <= 1064; i++) Emit(provider, i); });
            await producer.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(release.Task.IsCompleted);
            Assert.False(canceled.Task.IsCompleted);
            release.TrySetResult();
            // A later marker observed after the blocked batch establishes that the queue drained;
            // retrying its enqueue is necessary while the bounded queue is still full.
            using var timeout = new CancellationTokenSource(Deadline);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (!receiver.Bodies.Any(body => body.Contains(Id(2000), StringComparison.Ordinal)))
            {
                Emit(provider, 2000);
                await timer.WaitForNextTickAsync(timeout.Token);
            }
        }
        finally
        {
            release.TrySetResult();
            await Task.Run(provider.Dispose).WaitAsync(Deadline);
        }
        var payload = string.Join("", receiver.Bodies);
        Assert.Contains(Id(1), payload);
        var received = Enumerable.Range(65, 1000).Count(i => payload.Contains(Id(i), StringComparison.Ordinal));
        Assert.InRange(received, 1, 999);
    }

    private static SyntheticOtlpLoggerProvider Provider(Receiver receiver) =>
        new(new SyntheticOtlpSettings(SyntheticOtlpRole.TestApi, new Uri(receiver.Origin)));
    private static string Id(int id) => id.ToString("x32");
    private static void Emit(SyntheticOtlpLoggerProvider provider, int id) =>
        provider.CreateLogger("TestApi.SyntheticApi").LogInformation(new EventId(1, "TechnicalCorrelationObserved"), Template, Id(id));
    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (!condition()) await timer.WaitForNextTickAsync(timeout.Token);
    }

    private sealed class Receiver : IAsyncDisposable
    {
        private readonly WebApplication app;
        public string Origin => Address(app) + "/";
        public ConcurrentQueue<string> Bodies { get; } = new();
        public ConcurrentQueue<(string Method, string Path)> Requests { get; } = new();
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Receiver(WebApplication app) => this.app = app;
        public static async Task<Receiver> Start(Func<HttpContext, Task<int>> respond, string? signalPath = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var receiver = new Receiver(app);
            app.Map("/{**path}", async context =>
            {
                receiver.Requests.Enqueue((context.Request.Method, context.Request.Path.Value ?? ""));
                using var body = new MemoryStream();
                var buffer = new byte[8192];
                int n;
                while ((n = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0)
                {
                    if (body.Length + n > 1024 * 1024) { context.Response.StatusCode = 413; return; }
                    body.Write(buffer, 0, n);
                }
                receiver.Bodies.Enqueue(Encoding.UTF8.GetString(body.ToArray()));
                if (signalPath is null || context.Request.Path == signalPath) receiver.Reached.TrySetResult();
                try { context.Response.StatusCode = await respond(context); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            });
            try { await app.StartAsync(); return receiver; }
            catch { await app.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() => await app.DisposeAsync().AsTask().WaitAsync(Deadline);
    }
}
