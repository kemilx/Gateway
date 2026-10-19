using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Gateway.Tests;

public sealed class LoopbackConfigurationTests
{
    private const string MalformedUrl = "configuration-private-url-marker";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact]
    public void ValidateServer_rejects_alternative_server_without_starting_it()
    {
        using var server = new AlternativeServer();

        var error = Assert.Throws<InvalidOperationException>(() => LoopbackListenerGuard.ValidateServer(server));

        Assert.Equal(LoopbackListenerGuard.Error, error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(0, server.StartCount);
    }

    [Fact]
    public void Register_rejects_alternative_listener_factory_without_binding()
    {
        var factory = new AlternativeListenerFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionListenerFactory>(factory);

        var error = Assert.Throws<InvalidOperationException>(() => LoopbackListenerGuard.Register(services));

        Assert.Equal(LoopbackListenerGuard.Error, error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(0, factory.BindCount);
    }

    [Fact]
    public async Task Malformed_url_reaches_kestrel_parser_with_valid_policy()
    {
        await using var app = GatewayHost.Build(["--urls", MalformedUrl], configure: builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(PolicyConfiguration.Defaults());
            builder.Configuration.AddCommandLine(["--urls", MalformedUrl]);
            builder.Logging.ClearProviders();
        });
        using var deadline = new CancellationTokenSource(Deadline);

        var error = await Assert.ThrowsAsync<FormatException>(() => app.StartAsync(deadline.Token));

        // A policy validation/guard/startup failure cannot satisfy this assertion.
        // This is Kestrel BindingAddress.Parse's diagnostic for a missing scheme.
        Assert.Equal($"Invalid url: '{MalformedUrl}'", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Executable_rejects_malformed_url_with_exit_one_and_generic_diagnostic_after_positive_control()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gateway-configuration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "pan-policy-defaults.json"),
                Path.Combine(directory, "appsettings.json"));
            // A unique upstream response identifies this child Gateway, rather than
            // accepting a coincidental 404 from an unrelated process on a reused port.
            var marker = "configuration-positive-" + Guid.NewGuid().ToString("N");
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var upstream = builder.Build();
            upstream.MapGet("/configuration-readiness", () => marker);
            using var deadline = new CancellationTokenSource(Deadline);
            await upstream.StartAsync(deadline.Token);
            var upstreamAddress = Assert.Single(upstream.Urls) + "/";

            int port;
            using (var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                reservation.ExclusiveAddressUse = true;
                reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
            }
            // The real executable hides bound addresses. The unavoidable release-to-
            // bind window is not retried; a collision must fail this positive control.
            await WithProcessAsync(directory, $"http://127.0.0.1:{port}", upstreamAddress,
                async (process, _, _) =>
                {
                    using var ready = new CancellationTokenSource(Deadline);
                    using var poll = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
                    using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                    while (true)
                    {
                        Assert.False(process.HasExited, "Positive control exited before HTTP readiness.");
                        try
                        {
                            var response = await client.GetStringAsync(
                                $"http://127.0.0.1:{port}/passthrough/configuration-readiness", ready.Token);
                            Assert.Equal(marker, response);
                            Assert.False(process.HasExited);
                            break;
                        }
                        catch (HttpRequestException)
                        {
                            // Poll a bounded readiness condition, not the whole test.
                            await poll.WaitForNextTickAsync(ready.Token);
                        }
                    }
                });

            // Identical executable, working directory, appsettings, upstream and
            // sanitized environment; only --urls changes from the passing control.
            await WithProcessAsync(directory, MalformedUrl, upstreamAddress,
                async (process, stdout, stderr) =>
                {
                    await process.WaitForExitAsync().WaitAsync(Deadline);
                    Assert.Equal(1, process.ExitCode);
                    Assert.Equal(string.Empty, await stdout);
                    Assert.Equal("Gateway could not start or run with the supplied configuration.",
                        (await stderr).Trim());
                    Assert.DoesNotContain(MalformedUrl, await stderr);
                });
            using var shutdown = new CancellationTokenSource(Deadline);
            await upstream.StopAsync(shutdown.Token);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WithProcessAsync(string directory, string url, string upstream,
        Func<Process, Task<string>, Task<string>, Task> verify)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(GatewayHost).Assembly.Location);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(url);
        start.ArgumentList.Add("--Upstream");
        start.ArgumentList.Add(upstream);
        // Allow only OS/runtime discovery basics: no inherited ASPNETCORE_,
        // DOTNET_, Kestrel__, policy, logging or other app configuration sources.
        var allowed = new HashSet<string>(["PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "HOME", "USERPROFILE"],
            StringComparer.OrdinalIgnoreCase);
        foreach (var key in start.Environment.Keys.Where(key => !allowed.Contains(key)).ToArray())
            start.Environment.Remove(key);

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await verify(process, stdout, stderr);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(Deadline);
            await Task.WhenAll(stdout, stderr).WaitAsync(Deadline);
        }
    }

    private sealed class AlternativeServer : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public int StartCount { get; private set; }
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull
        {
            StartCount++;
            throw new InvalidOperationException("Alternative server must not start.");
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class AlternativeListenerFactory : IConnectionListenerFactory
    {
        public int BindCount { get; private set; }
        public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            BindCount++;
            throw new InvalidOperationException("Alternative listener must not bind.");
        }
    }
}
