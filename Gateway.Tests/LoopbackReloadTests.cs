using System.Net;
using System.Text.Json;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Gateway.Tests;

public sealed class LoopbackReloadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private const string WildcardUrl = "http://0.0.0.0:0";

    [Fact]
    public async Task File_reload_applies_loopback_change_then_rejects_wildcard_at_transport()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gateway-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "listeners.json");
            await WriteEndpointAsync(path, "loopbackA", "http://127.0.0.1:0");
            using var files = new PhysicalFileProvider(directory);
            using var logs = new ReloadFailureProvider();
            var loopbackB = new TaskCompletionSource<ListenOptions>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var app = GatewayHost.Build([], configure: builder =>
            {
                builder.Configuration.Sources.Clear();
                builder.Configuration.AddInMemoryCollection(PolicyConfiguration.Defaults());
                builder.Configuration.AddJsonFile(files, "listeners.json", optional: false, reloadOnChange: true);
                builder.WebHost.ConfigureKestrel(options =>
                    options.Configure(builder.Configuration.GetSection("Kestrel"), reloadOnChange: true)
                        .Endpoint("loopbackB", endpoint => loopbackB.TrySetResult(endpoint.ListenOptions)));

                // Production deliberately suppresses framework logs. Override only this
                // host, with no console/file provider and no retained exception objects.
                builder.Logging.ClearProviders();
                builder.Logging.Services.Configure<LoggerFilterOptions>(options =>
                {
                    options.Rules.Clear();
                    options.MinLevel = LogLevel.Critical;
                });
                builder.Logging.AddProvider(logs);
            });
            app.MapGet("/reload-test", () => "reload-loopback");
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout };
            try
            {
                using (var startup = new CancellationTokenSource(Timeout))
                    await app.StartAsync(startup.Token).WaitAsync(Timeout);
                var initialAddress = Assert.Single(app.Urls);
                Assert.Equal("127.0.0.1", new Uri(initialAddress).Host);
                Assert.Equal("reload-loopback", await client.GetStringAsync(initialAddress + "/reload-test"));
                Assert.Equal(0, logs.GuardFailureCount);

                // Different endpoint name AND loopback IP force a real replacement;
                // both binds use port 0, avoiding free-port reservation/reuse races.
                await WriteEndpointAsync(path, "loopbackB", "http://127.0.0.2:0");
                var replacement = await loopbackB.Task.WaitAsync(Timeout);
                var bound = await WaitForBoundEndpointAsync(replacement).WaitAsync(Timeout);
                Assert.Equal(IPAddress.Parse("127.0.0.2"), bound.Address);
                Assert.InRange(bound.Port, 1, 65535);
                Assert.Equal("reload-loopback",
                    await client.GetStringAsync($"http://{bound}/reload-test"));
                Assert.Equal(0, logs.GuardFailureCount);

                await WriteEndpointAsync(path, "wildcardC", WildcardUrl);
                var signal = await logs.GuardFailure.Task.WaitAsync(Timeout);
                Assert.Equal(LogLevel.Critical, signal.Level);
                Assert.Equal(0, signal.EventId);
                Assert.Equal(1, logs.GuardFailureCount);
                // A wildcard port-0 rejection has no concrete port to probe. The
                // guard exception (not an address-in-use error or missing URL) is
                // the essential evidence that rejection preceded the inner bind.
                // Kestrel may already have stopped loopbackB; continuity is not required.
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(Timeout);
                await app.StopAsync(shutdown.Token).WaitAsync(Timeout);
            }
            // Stop joins any concurrent rebind before checking the final count.
            Assert.Equal(1, logs.GuardFailureCount);
        }
        finally
        {
            // The host and explicitly owned watcher have been disposed first,
            // including when startup, a readiness condition, or an assertion fails.
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WriteEndpointAsync(string path, string name, string url)
    {
        var json = JsonSerializer.Serialize(new
        {
            Kestrel = new
            {
                Endpoints = new Dictionary<string, object>
                {
                    [name] = new { Url = url, Protocols = "Http1" }
                }
            }
        });
        // Publish a complete file; the JSON watcher must never see a partial write.
        var staged = path + ".staged";
        await File.WriteAllTextAsync(staged, json);
        File.Move(staged, path, overwrite: true);
    }

    private static async Task<IPEndPoint> WaitForBoundEndpointAsync(ListenOptions options)
    {
        using var deadline = new CancellationTokenSource(Timeout);
        using var poll = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        // The endpoint callback precedes binding. Poll only this observable
        // readiness condition, never retry the test or assume elapsed time proves bind.
        while (true)
        {
            if (options.IPEndPoint is { Port: > 0 } endpoint)
                return endpoint;
            await poll.WaitForNextTickAsync(deadline.Token);
        }
    }

    private sealed class ReloadFailureProvider : ILoggerProvider
    {
        private int _guardFailureCount;
        public int GuardFailureCount => Volatile.Read(ref _guardFailureCount);
        public TaskCompletionSource<(LogLevel Level, int EventId)> GuardFailure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName) => new ReloadLogger(this, categoryName);
        public void Dispose() { }

        private sealed class ReloadLogger(ReloadFailureProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level == LogLevel.Critical
                && category == "Microsoft.AspNetCore.Server.Kestrel";

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel) || eventId.Id != 0 || !ContainsGuardFailure(exception)
                    || state is not IEnumerable<KeyValuePair<string, object?>> properties)
                    return;

                var fields = properties.ToArray();
                if (!fields.Any(pair => pair.Key == "{OriginalFormat}"
                        && Equals(pair.Value, "Unable to bind to '{url}' on config reload."))
                    || !fields.Any(pair => pair.Key == "url" && Equals(pair.Value, WildcardUrl)))
                    return;

                Interlocked.Increment(ref owner._guardFailureCount);
                owner.GuardFailure.TrySetResult((logLevel, eventId.Id));
            }

            private static bool ContainsGuardFailure(Exception? exception)
            {
                if (exception is null) return false;
                if (exception is InvalidOperationException && exception.Message == LoopbackListenerGuard.Error)
                    return true;
                if (exception is AggregateException aggregate)
                    return aggregate.InnerExceptions.Any(ContainsGuardFailure);
                return ContainsGuardFailure(exception.InnerException);
            }
        }
    }
}
