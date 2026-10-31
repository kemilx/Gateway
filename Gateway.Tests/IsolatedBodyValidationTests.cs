using System.Net;
using System.Text;
using Gateway.LoadHarness;
using Xunit;

namespace Gateway.Tests;

public sealed class IsolatedBodyValidationTests
{
    private static readonly byte[] Expected = CompareHarness.Expected(true);

    [Fact]
    public async Task Load_accepts_exact_masked_payload()
    {
        using var client = Client(Expected);
        Assert.Equal(200, await IsolatedHarness.Request(client, "/protected/load", Expected, CancellationToken.None));
    }

    [Fact]
    public async Task Load_rejects_invalid_json_with_same_length_and_mask()
    {
        var body = (byte[])Expected.Clone();
        body[0] = (byte)'!';
        using var client = Client(body);
        var failure = await Assert.ThrowsAsync<CompareFailure>(() => IsolatedHarness.Request(client, "/protected/load", Expected, CancellationToken.None));
        Assert.Equal("body_invalid", failure.Code);
    }

    [Fact]
    public async Task Load_rejects_modified_nonsensitive_field_with_same_length_and_mask()
    {
        var body = (byte[])Expected.Clone();
        var index = body.AsSpan().IndexOf("\"a\":1"u8);
        Assert.True(index >= 0);
        body[index + 4] = (byte)'2';
        using var client = Client(body);
        var failure = await Assert.ThrowsAsync<CompareFailure>(() => IsolatedHarness.Request(client, "/protected/load", Expected, CancellationToken.None));
        Assert.Equal("body_invalid", failure.Code);
    }

    [Fact]
    public async Task Load_rejects_wrong_size()
    {
        using var client = Client(Expected[..^1]);
        var failure = await Assert.ThrowsAsync<CompareFailure>(() => IsolatedHarness.Request(client, "/protected/load", Expected, CancellationToken.None));
        Assert.Equal("body_invalid", failure.Code);
        using var oversized = Client([.. Expected, 0]);
        failure = await Assert.ThrowsAsync<CompareFailure>(() => IsolatedHarness.Request(oversized, "/protected/load", Expected, CancellationToken.None));
        Assert.Equal("body_invalid", failure.Code);
    }

    [Fact]
    public async Task Load_rejects_wrong_status()
    {
        using var client = Client(Expected, HttpStatusCode.BadRequest);
        var failure = await Assert.ThrowsAsync<CompareFailure>(() => IsolatedHarness.Request(client, "/protected/load", Expected, CancellationToken.None));
        Assert.Equal("body_invalid", failure.Code);
    }

    [Fact]
    public async Task Payment_retains_canary_validation()
    {
        using var client = Client(Encoding.UTF8.GetBytes("prefix ************1111 suffix"));
        Assert.Equal(200, await IsolatedHarness.Request(client, "/protected/payment", null, CancellationToken.None));
        using var invalid = Client(Encoding.UTF8.GetBytes("no mask"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => IsolatedHarness.Request(invalid, "/protected/payment", null, CancellationToken.None));
    }

    private static HttpClient Client(byte[] body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new ResponseHandler(body, status)) { BaseAddress = new Uri("http://localhost/") };

    private sealed class ResponseHandler(byte[] body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
    }
}
