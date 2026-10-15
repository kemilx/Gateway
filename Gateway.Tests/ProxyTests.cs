using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestApi;
using Xunit;

namespace Gateway.Tests;

public sealed class ProxyTests
{
    [Fact]
    public async Task Real_yarp_hop_masks_only_selected_field_and_repairs_metadata()
    {
        await using var hosts = await Hosts.Start();
        var direct = await hosts.Client.GetStringAsync(hosts.Upstream + "/payment");
        Assert.Contains(SyntheticApi.Pan, direct);
        using var response = await hosts.Client.GetAsync("/protected/payment?synthetic=private-query-marker");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("test-api", Assert.Single(response.Headers.GetValues("X-Synthetic-Upstream")));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var actual = JsonNode.Parse(bytes)!;
        var expected = JsonNode.Parse(SyntheticApi.Sample)!;
        expected["payment"]!["cardNumber"] = "************1111";
        Assert.True(JsonNode.DeepEquals(expected, actual));
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.Null(response.Headers.ETag);
        Assert.False(response.Headers.Contains("Content-Digest"));
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Contains(hosts.Logs.Messages, x => x.Contains("response inspected"));
        Assert.DoesNotContain(hosts.Logs.Messages, x => x.Contains(SyntheticApi.Pan) || x.Contains("synthetic-ñ")
            || x.Contains("cardNumber") || x.Contains("private-query-marker"));
    }

    [Theory]
    [InlineData("{\"payment\":{\"cardNumber\":\"4111-1111-1111-1111\"}}", "****-****-****-1111")]
    [InlineData("{\"payment\":{\"cardNumber\":\"1234\"}}", "[REDACTED]")]
    [InlineData("{\"payment\":{\"cardNumber\":\"bad-value\"}}", "[REDACTED]")]
    [InlineData("{\"payment\":{\"cardNumber\":null}}", null)]
    public async Task String_and_null_contract(string body, string? expected)
    {
        await using var hosts = await Hosts.Start(app => MapBody(app, body));
        var actual = JsonNode.Parse(await hosts.Client.GetStringAsync("/protected/case"));
        Assert.Equal(expected, actual!["payment"]!["cardNumber"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"payment\":{\"cardNumber\":4111111111111111}}")]
    [InlineData("{\"payment\":{\"cardNumber\":{\"secret\":\"4111111111111111\"}}}")]
    [InlineData("{\"payment\":{\"cardNumber\":\"4111111111111111\"}},")]
    [InlineData("{\"payment\":{\"cardNumber\":\"4111111111111111\"},\"late\":")]
    [InlineData("{\"payment\":{\"cardNumber\":\"\\uD800\"}}")]
    public async Task Invalid_json_or_unexpected_sensitive_type_is_closed(string body)
    {
        await using var hosts = await Hosts.Start(app => MapBody(app, body));
        await AssertBlocked(hosts);
        Assert.Contains(hosts.Logs.Messages, x => x.Contains("response blocked"));
        Assert.DoesNotContain(hosts.Logs.Messages, x => x.Contains(SyntheticApi.Pan) || x.Contains("cardNumber"));
    }

    [Theory]
    [InlineData(1048575, false)]
    [InlineData(1048576, false)]
    [InlineData(1048577, false)]
    [InlineData(1048577, true)]
    public async Task Real_size_limit_including_fragmented_without_content_length(int size, bool declared)
    {
        var prefix = "{\"payment\":{\"cardNumber\":\"4111111111111111\"},\"padding\":\"";
        var body = prefix + new string('x', size - Encoding.UTF8.GetByteCount(prefix) - 2) + "\"}";
        await using var hosts = await Hosts.Start(app => app.MapGet("/case", async context =>
        {
            context.Response.ContentType = "application/json";
            if (declared) context.Response.ContentLength = size;
            var bytes = Encoding.UTF8.GetBytes(body);
            for (var offset = 0; offset < bytes.Length; offset += 4093)
            {
                await context.Response.Body.WriteAsync(bytes.AsMemory(offset, Math.Min(4093, bytes.Length - offset)));
                await context.Response.Body.FlushAsync();
            }
        }));
        if (size > PanResponseTransform.MaxBodyBytes) await AssertBlocked(hosts);
        else
        {
            var result = await hosts.Client.GetStringAsync("/protected/case");
            Assert.DoesNotContain(SyntheticApi.Pan, result);
            Assert.Equal("************1111", JsonNode.Parse(result)!["payment"]!["cardNumber"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task No_headers_or_body_are_delivered_before_late_parse_failure()
    {
        var firstFlushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hosts = await Hosts.Start(app => app.MapGet("/case", async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"payment\":{\"cardNumber\":\"4111111111111111\"},\"late\":");
            await context.Response.Body.FlushAsync();
            firstFlushed.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await context.Response.WriteAsync("INVALID}");
        }));
        var pending = hosts.Client.GetAsync("/protected/case", HttpCompletionOption.ResponseHeadersRead);
        try
        {
            await firstFlushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(200);
            Assert.False(pending.IsCompleted); // Even downstream headers have not started.
        }
        finally { release.TrySetResult(); }
        using var result = await pending;
        Assert.Equal(HttpStatusCode.BadGateway, result.StatusCode);
        Assert.Equal("{\"code\":\"privacy_inspection_failed\"}", await result.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Partial_content_json_is_closed_without_upstream_body_or_metadata()
    {
        await using var hosts = await Hosts.Start(app => app.MapGet("/case", async context =>
        {
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = Encoding.UTF8.GetByteCount(SyntheticApi.Sample);
            context.Response.Headers.ContentRange = $"bytes 0-{context.Response.ContentLength - 1}/{context.Response.ContentLength + 1}";
            context.Response.Headers.AcceptRanges = "bytes";
            context.Response.Headers.ETag = "\"raw-partial\"";
            context.Response.Headers.LastModified = "Wed, 14 Oct 2026 12:00:00 GMT";
            context.Response.Headers["Content-Digest"] = "synthetic-upstream-digest";
            context.Response.Headers["X-Sensitive-Upstream"] = SyntheticApi.Pan;
            await context.Response.WriteAsync(SyntheticApi.Sample);
        }));
        using var response = await hosts.Client.GetAsync("/protected/case");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("{\"code\":\"privacy_inspection_failed\"}", body);
        Assert.DoesNotContain(SyntheticApi.Pan, body);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), response.Content.Headers.ContentLength);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.True(response.Headers.CacheControl!.NoStore);
        foreach (var header in new[] { "ETag", "Last-Modified", "Content-Digest", "Content-Range",
                     "Accept-Ranges", "Content-Encoding", "Transfer-Encoding", "X-Sensitive-Upstream" })
        {
            Assert.DoesNotContain(response.Headers.Concat(response.Content.Headers),
                entry => string.Equals(entry.Key, header, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Interrupted_upstream_body_is_closed_without_early_headers_or_body()
    {
        var firstFlushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hosts = await Hosts.Start(app => app.MapGet("/case", async context =>
        {
            context.Response.ContentType = "application/json";
            // Valid JSON, but the declared final newline is never sent: this exercises a transport
            // failure, not a JSON parse failure or a connection closed before response headers.
            context.Response.ContentLength = Encoding.UTF8.GetByteCount(SyntheticApi.Sample) + 1;
            context.Response.Headers["X-Sensitive-Upstream"] = SyntheticApi.Pan;
            context.Response.Headers.ETag = "\"raw-interrupted\"";
            await context.Response.WriteAsync(SyntheticApi.Sample);
            await context.Response.Body.FlushAsync();
            firstFlushed.TrySetResult();
            try { await abort.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { context.Abort(); }
        }));
        var pending = hosts.Client.GetAsync("/protected/case", HttpCompletionOption.ResponseHeadersRead);
        try
        {
            await firstFlushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // A negative observation needs a bounded window. ResponseHeadersRead would complete
            // on any downstream headers; this timeout asserts silence while upstream is held open.
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await pending.WaitAsync(TimeSpan.FromMilliseconds(250)));
        }
        finally { abort.TrySetResult(); }
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        var body = await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("{\"code\":\"privacy_inspection_failed\"}", body);
        Assert.DoesNotContain(SyntheticApi.Pan, body);
        Assert.False(response.Headers.Contains("X-Sensitive-Upstream"));
        Assert.Null(response.Headers.ETag);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), response.Content.Headers.ContentLength);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Contains(hosts.Logs.Messages, x => x.Contains("response blocked"));
    }

    [Fact]
    public async Task Invalid_utf8_is_closed_without_echoing_body()
    {
        await using var hosts = await Hosts.Start(app => app.MapGet("/case", async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(new byte[] { (byte)'{', (byte)'"', (byte)'x', (byte)'"',
                (byte)':', (byte)'"', 0xff, (byte)'"', (byte)'}' });
        }));
        await AssertBlocked(hosts);
    }

    [Fact]
    public async Task Duplicate_selected_properties_are_all_masked_and_other_paths_untouched()
    {
        const string body = "{\"payment\":{\"cardNumber\":\"4111111111111111\",\"cardNumber\":\"5555555555554444\"},\"payment\":{\"cardNumber\":\"4111111111111111\"},\"nested\":{\"cardNumber\":\"other-field\"},\"Payment\":{\"cardNumber\":\"case-sensitive\"}}";
        await using var hosts = await Hosts.Start(app => MapBody(app, body));
        var result = await hosts.Client.GetStringAsync("/protected/case");
        Assert.Equal(body.Replace(SyntheticApi.Pan, "************1111").Replace("5555555555554444", "************4444"), result);
    }

    [Fact]
    public async Task Non_json_and_explicit_passthrough_are_not_inspected()
    {
        await using var hosts = await Hosts.Start(app => MapBody(app, "not-json-" + SyntheticApi.Pan, "text/plain"));
        Assert.Equal("not-json-" + SyntheticApi.Pan, await hosts.Client.GetStringAsync("/protected/case"));
        Assert.Contains(SyntheticApi.Pan, await hosts.Client.GetStringAsync("/passthrough/payment"));
        Assert.DoesNotContain(hosts.Logs.Messages, x => x.Contains("response inspected"));
    }

    [Theory]
    [InlineData("encoding")]
    [InlineData("charset")]
    [InlineData("depth")]
    public async Task Unsupported_json_encoding_and_depth_fail_closed(string scenario)
    {
        await using var hosts = await Hosts.Start(app => app.MapGet("/case", async context =>
        {
            context.Response.ContentType = scenario == "charset" ? "application/json; charset=iso-8859-1" : "application/json";
            if (scenario == "encoding") context.Response.Headers.ContentEncoding = "gzip";
            await context.Response.WriteAsync(scenario == "depth" ? new string('[', 65) + "0" + new string(']', 65) : SyntheticApi.Sample);
        }));
        await AssertBlocked(hosts);
    }

    [Theory]
    [InlineData(204)]
    [InlineData(304)]
    public async Task Bodyless_status_remains_bodyless(int status)
    {
        await using var hosts = await Hosts.Start(app => app.MapGet("/case", context =>
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            return Task.CompletedTask;
        }));
        using var result = await hosts.Client.GetAsync("/protected/case");
        Assert.Equal(status, (int)result.StatusCode);
        Assert.Empty(await result.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Protected_requests_disable_compression_conditionals_and_ranges()
    {
        await using var hosts = await Hosts.Start(app => app.MapGet("/case", async context =>
        {
            Assert.Equal("identity", context.Request.Headers.AcceptEncoding.ToString());
            Assert.Empty(context.Request.Headers.IfNoneMatch.ToString());
            Assert.Empty(context.Request.Headers.IfModifiedSince.ToString());
            Assert.Empty(context.Request.Headers.Range.ToString());
            Assert.Empty(context.Request.Headers.IfRange.ToString());
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(SyntheticApi.Sample);
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/protected/case");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"raw-v1\"");
        request.Headers.TryAddWithoutValidation("If-Modified-Since", "Wed, 14 Oct 2026 12:00:00 GMT");
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-10");
        request.Headers.TryAddWithoutValidation("If-Range", "\"raw-v1\"");
        using var response = await hosts.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(SyntheticApi.Pan, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Head_remains_bodyless_and_error_json_is_masked()
    {
        await using var hosts = await Hosts.Start(app => app.MapMethods("/case", ["GET", "HEAD"], async context =>
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(SyntheticApi.Sample);
        }));
        using var head = await hosts.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/protected/case"));
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        using var error = await hosts.Client.GetAsync("/protected/case");
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.DoesNotContain(SyntheticApi.Pan, await error.Content.ReadAsStringAsync());
    }

    private static void MapBody(WebApplication app, string body, string contentType = "application/json") =>
        app.MapGet("/case", async context =>
        {
            context.Response.ContentType = contentType;
            context.Response.Headers.ETag = "\"raw\"";
            await context.Response.WriteAsync(body);
        });

    private static async Task AssertBlocked(Hosts hosts)
    {
        using var response = await hosts.Client.GetAsync("/protected/case");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("{\"code\":\"privacy_inspection_failed\"}", await response.Content.ReadAsStringAsync());
        Assert.Null(response.Headers.ETag);
        Assert.False(response.Content.Headers.Contains("Content-Encoding"));
    }

    private sealed class Hosts(WebApplication api, WebApplication gateway, string upstream, CaptureLogs logs) : IAsyncDisposable
    {
        public string Upstream { get; } = upstream;
        public CaptureLogs Logs { get; } = logs;
        public HttpClient Client { get; } = new(new SocketsHttpHandler { UseProxy = false })
        { BaseAddress = new Uri(Address(gateway)), Timeout = TimeSpan.FromSeconds(15) };

        public static async Task<Hosts> Start(Action<WebApplication>? endpoints = null)
        {
            var api = SyntheticApi.Build([], b => b.WebHost.UseUrls("http://127.0.0.1:0"), endpoints);
            await api.StartAsync();
            var logs = new CaptureLogs();
            var gateway = GatewayHost.Build([], Address(api) + "/", b =>
            {
                b.WebHost.UseUrls("http://127.0.0.1:0");
                b.Logging.AddProvider(logs);
            });
            try { await gateway.StartAsync(); }
            catch { await gateway.DisposeAsync(); await api.DisposeAsync(); throw; }
            return new Hosts(api, gateway, Address(api), logs);
        }

        private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await gateway.StopAsync();
            await gateway.DisposeAsync();
            await api.StopAsync();
            await api.DisposeAsync();
        }
    }

    private sealed class CaptureLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);
        public void Dispose() { }
        private sealed class CaptureLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }
}
