using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

public sealed class LoopbackListenerTests
{
    private static WebApplication Build(string[]? args = null, Action<WebApplicationBuilder>? configure = null)
        => GatewayHost.Build(args ?? [], configure: b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(PolicyConfiguration.Defaults());
            b.Configuration.AddCommandLine(args ?? []);
            configure?.Invoke(b);
        });

    [Theory]
    [InlineData("http://0.0.0.0:0")]
    [InlineData("http://[::]:0")]
    [InlineData("http://*:0")]
    [InlineData("http://+:0")]
    [InlineData("http://untrusted.invalid:0")]
    [InlineData("http://192.0.2.1:0")]
    [InlineData("http://127.0.0.1:0;http://0.0.0.0:0")]
    public async Task Unsafe_urls_fail_at_transport_with_sanitized_error(string urls)
    {
        await using var app = Build(["--urls", urls]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Equal(LoopbackListenerGuard.Error, error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://127.0.0.2:0")]
    [InlineData("http://[::1]:0")]
    public async Task Loopback_ephemeral_listener_serves_requests(string url)
    {
        await using var app = Build(["--urls", url]);
        app.MapGet("/listener-test", () => "loopback");
        await app.StartAsync();
        var address = Assert.Single(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses);
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(new Uri(address).Host.Trim('[', ']'))));
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        Assert.Equal("loopback", await client.GetStringAsync(address + "/listener-test"));
        await app.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Effective_kestrel_endpoint_overrides_safe_urls_and_is_rejected(bool codeOverride)
    {
        await using var app = Build(["--urls", "http://127.0.0.1:0"], b =>
        {
            if (codeOverride) b.WebHost.ConfigureKestrel(k => k.ListenAnyIP(0));
            else b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Unsafe:Url"] = "http://0.0.0.0:0"
            });
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Equal(LoopbackListenerGuard.Error, error.Message);
    }

    [Fact]
    public async Task Safe_kestrel_override_is_allowed_despite_unused_wildcard_urls()
    {
        await using var app = Build(["--urls", "http://*:0"], b =>
            b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0)));
        await app.StartAsync();
        Assert.StartsWith("http://127.0.0.1:", Assert.Single(app.Urls));
        await app.StopAsync();
    }

    [Theory]
    [InlineData("unix")]
    [InlineData("handle")]
    [InlineData("mapped")]
    [InlineData("pipe")]
    public async Task Unverifiable_or_unsupported_bindings_fail_closed(string kind)
    {
        await using var app = Build(configure: b => b.WebHost.ConfigureKestrel(k =>
        {
            switch (kind)
            {
                case "unix": k.ListenUnixSocket(Path.Combine(Path.GetTempPath(), "gateway-test.sock")); break;
                case "handle": k.ListenHandle(123); break;
                case "mapped": k.Listen(IPAddress.Parse("::ffff:127.0.0.1"), 0); break;
                case "pipe": k.ListenNamedPipe("gateway-listener-test"); break;
            }
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Equal(LoopbackListenerGuard.Error, error.Message);
    }

    [Fact]
    public async Task Multiplexed_transport_fails_closed_before_binding()
    {
        await using var app = Build();
        foreach (var factory in app.Services.GetServices<Microsoft.AspNetCore.Connections.IMultiplexedConnectionListenerFactory>())
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await factory.BindAsync(new IPEndPoint(IPAddress.Loopback, 0)));
            Assert.Equal(LoopbackListenerGuard.Error, error.Message);
        }
        Assert.NotEmpty(app.Services.GetServices<Microsoft.AspNetCore.Connections.IMultiplexedConnectionListenerFactory>());
    }

    [Fact]
    public async Task Custom_socket_creator_is_rejected_without_invocation()
    {
        var invoked = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var app = Build(configure: b => b.Services.Configure<SocketTransportOptions>(o =>
                o.CreateBoundListenSocket = endpoint =>
                {
                    invoked = true;
                    throw new Exception("must not execute");
                }));
            await app.StartAsync();
        });
        Assert.Equal(LoopbackListenerGuard.Error, error.Message);
        Assert.False(invoked);
    }

    [Theory]
    [InlineData("ASPNETCORE_URLS")]
    [InlineData("DOTNET_URLS")]
    [InlineData("ASPNETCORE_HTTP_PORTS")]
    [InlineData("ASPNETCORE_HTTPS_PORTS")]
    [InlineData("Kestrel__Endpoints__Injected__Url")]
    public async Task Real_environment_sources_cannot_expose_listener(string key)
        => await WithEnvironmentProcess(key,
            key.EndsWith("PORTS", StringComparison.Ordinal) ? "0" : "http://0.0.0.0:0",
            async (process, stdout, stderr, _) =>
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.NotEqual(0, process.ExitCode);
                Assert.Equal("Gateway could not start or run with the supplied configuration.", (await stderr).Trim());
                Assert.DoesNotContain("Now listening", await stdout);
            });

    [Fact]
    public async Task Real_environment_loopback_baseline_starts_and_serves_https()
    {
        int port;
        using (var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        }
        await WithEnvironmentProcess("ASPNETCORE_URLS", $"https://127.0.0.1:{port}",
            async (process, _, _, certificate) =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var client = new HttpClient(new HttpClientHandler
                {
                    UseProxy = false,
                    ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual?.Thumbprint == certificate.Thumbprint
                });
                while (true)
                {
                    Assert.False(process.HasExited, "Loopback baseline exited before readiness.");
                    try
                    {
                        // Unmatched route needs no upstream; 404 proves the real
                        // executable reached the HTTP pipeline over the test certificate.
                        using var response = await client.GetAsync($"https://127.0.0.1:{port}/listener-readiness", deadline.Token);
                        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                        Assert.False(process.HasExited);
                        break;
                    }
                    catch (HttpRequestException)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
                    }
                }
            });
    }

    private static async Task WithEnvironmentProcess(string key, string value,
        Func<Process, Task<string>, Task<string>, X509Certificate2, Task> verify)
    {
        var directory = Path.Combine(Path.GetTempPath(), "gateway-listener-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "pan-policy-defaults.json"), Path.Combine(directory, "appsettings.json"));
            using var certificate = CreateCertificate();
            var certificatePath = Path.Combine(directory, "local.pfx");
            File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx));
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(typeof(GatewayHost).Assembly.Location);
            foreach (var inherited in start.Environment.Keys.Where(k =>
                         k.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
                         || k.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
                         || k.StartsWith("Kestrel__", StringComparison.OrdinalIgnoreCase)
                         || k is "URLS" or "HTTP_PORTS" or "HTTPS_PORTS").ToArray())
                start.Environment.Remove(inherited);
            start.Environment["Kestrel__Certificates__Default__Path"] = certificatePath;
            start.Environment[key] = value;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await verify(process, stdout, stderr, certificate);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                await Task.WhenAll(stdout, stderr);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Localhost_http_and_https_profile_remains_supported()
    {
        static int FreePort()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)socket.LocalEndPoint!).Port;
        }
        using var certificate = CreateCertificate();
        var http = $"http://localhost:{FreePort()}";
        var https = $"https://localhost:{FreePort()}";
        await using var app = Build(["--urls", http + ";" + https], b =>
            b.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(o => o.ServerCertificate = certificate)));
        app.MapGet("/listener-test", () => "local-profile");
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual?.Thumbprint == certificate.Thumbprint
        });
        Assert.Equal("local-profile", await client.GetStringAsync(http + "/listener-test"));
        Assert.Equal("local-profile", await client.GetStringAsync(https + "/listener-test"));
        await app.StopAsync();
    }

    [Fact]
    public async Task PreferHostingUrls_cannot_override_safe_endpoint_with_wildcard()
    {
        await using var app = Build(["--urls", "http://*:0"], b =>
        {
            b.WebHost.PreferHostingUrls(true);
            b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Equal(LoopbackListenerGuard.Error, error.Message);
    }

    [Fact]
    public async Task Transport_guard_rechecks_every_bind_not_only_startup()
    {
        await using var app = Build();
        var factory = Assert.Single(app.Services.GetServices<Microsoft.AspNetCore.Connections.IConnectionListenerFactory>());
        await using var listener = await factory.BindAsync(new IPEndPoint(IPAddress.Loopback, 0));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await factory.BindAsync(new IPEndPoint(IPAddress.Any, 0)));
        Assert.Equal(LoopbackListenerGuard.Error, error.Message);
        await listener.UnbindAsync();
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable);
    }
}
