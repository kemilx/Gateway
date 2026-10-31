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
using Xunit;

namespace Gateway.Tests;

public sealed class ForwardingContractTests
{
    [Theory]
    [InlineData("GET", false)] [InlineData("GET", true)]
    [InlineData("POST", false)] [InlineData("POST", true)]
    [InlineData("PUT", false)] [InlineData("PUT", true)]
    [InlineData("PATCH", false)] [InlineData("PATCH", true)]
    [InlineData("DELETE", false)] [InlineData("DELETE", true)]
    [InlineData("OPTIONS", false)] [InlineData("OPTIONS", true)]
    [InlineData("HEAD", false)] [InlineData("HEAD", true)]
    public async Task Protected_request_forwards_method_query_and_exact_uninspected_body(string method, bool binary)
    {
        const string query = "?a=one&a=two&escaped=%2F%20%C3%B1&empty=";
        var requestBytes = binary ? new byte[] { 0, 255, 128, 13, 10, 34, 92, 1 } : Encoding.UTF8.GetBytes("{\"payment\":\"4111111111111111\",\"note\":\"ñ😀\"}");
        var seen = new TaskCompletionSource<(string Method, string Path, string Query, byte[] Body)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = WebApplication.CreateBuilder(); b.Logging.ClearProviders(); b.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = b.Build();
        api.MapMethods("/forward", ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD"], async (HttpContext c) =>
        {
            using var body = new MemoryStream(); await c.Request.Body.CopyToAsync(body);
            seen.TrySetResult((c.Request.Method, c.Request.Path.Value!, c.Request.QueryString.Value!, body.ToArray()));
            c.Response.ContentType = "application/json";
            if (!HttpMethods.IsHead(c.Request.Method)) await c.Response.WriteAsync("{\"payment\":{\"cardNumber\":\"4111111111111111\"}}");
        });
        await api.StartAsync();
        await using var gateway = GatewayHost.Build([], Address(api) + "/", x =>
        {
            x.Configuration.Sources.Clear(); x.Configuration.AddInMemoryCollection(PolicyConfiguration.Defaults());
            x.WebHost.UseUrls("http://127.0.0.1:0"); x.Logging.ClearProviders();
        });
        await gateway.StartAsync();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(Address(gateway)), Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(new HttpMethod(method), "/protected/forward" + query) { Content = new ByteArrayContent(requestBytes) };
        request.Content.Headers.ContentType = new(binary ? "application/octet-stream" : "application/json") { CharSet = binary ? null : "utf-8" };
        using var response = await client.SendAsync(request);
        var observed = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(method, observed.Method); Assert.Equal("/forward", observed.Path); Assert.Equal(query, observed.Query);
        Assert.Equal(requestBytes, observed.Body);
        Assert.Equal(200, (int)response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(method == "HEAD" ? "" : "{\"payment\":{\"cardNumber\":\"************1111\"}}", text);
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
}
