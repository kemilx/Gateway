using System.Net;
using System.Text;
using System.Text.Json;
using Gateway.LoadHarness;
using Xunit;

namespace Gateway.Tests;

[Collection("Isolated harness")]
public sealed class HttpBodyValidationTests
{
    private static readonly byte[] Expected = CompareHarness.Expected(true);

    [Fact]
    public async Task Load_rejects_invalid_json_with_same_length_and_mask()
    {
        var body = (byte[])Expected.Clone();
        body[0] = (byte)'!';
        using var client = Client(body);
        var failure = await Assert.ThrowsAsync<CompareFailure>(() => HttpHarness.Request(client, "/protected/load", Expected, CancellationToken.None));
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
        var failure = await Assert.ThrowsAsync<CompareFailure>(() => HttpHarness.Request(client, "/protected/load", Expected, CancellationToken.None));
        Assert.Equal("body_invalid", failure.Code);
    }

    [Fact]
    public async Task Load_accepts_exact_masked_payload()
    {
        using var client = Client(Expected);
        Assert.Equal(200, await HttpHarness.Request(client, "/protected/load", Expected, CancellationToken.None));
    }

    [Fact]
    public async Task Load_rejects_wrong_size()
    {
        using var shortClient = Client(Expected[..^1]);
        var failure = await Assert.ThrowsAsync<CompareFailure>(() => HttpHarness.Request(shortClient, "/protected/load", Expected, CancellationToken.None));
        Assert.Equal("body_invalid", failure.Code);
        using var longClient = Client([.. Expected, 0]);
        failure = await Assert.ThrowsAsync<CompareFailure>(() => HttpHarness.Request(longClient, "/protected/load", Expected, CancellationToken.None));
        Assert.Equal("body_invalid", failure.Code);
    }

    [Fact]
    public async Task Load_rejects_wrong_status()
    {
        using var client = Client(Expected, HttpStatusCode.BadRequest);
        var failure = await Assert.ThrowsAsync<CompareFailure>(() => HttpHarness.Request(client, "/protected/load", Expected, CancellationToken.None));
        Assert.Equal("body_invalid", failure.Code);
    }

    [Fact]
    public async Task Payment_retains_canary_and_status_validation()
    {
        var body = Encoding.UTF8.GetBytes("prefix ************1111 suffix");
        using var client = Client(body);
        Assert.Equal(200, await HttpHarness.Request(client, "/protected/payment", null, CancellationToken.None));
        using var invalid = Client(Encoding.UTF8.GetBytes("no mask"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => HttpHarness.Request(invalid, "/protected/payment", null, CancellationToken.None));
        using var badStatus = Client(body, HttpStatusCode.BadRequest);
        await Assert.ThrowsAsync<InvalidOperationException>(() => HttpHarness.Request(badStatus, "/protected/payment", null, CancellationToken.None));
    }

    [Fact]
    public async Task Run_reports_all_real_http_scenarios_as_passed()
    {
        var output = Path.Combine(Path.GetTempPath(), "opencode", "http-body-fix", Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await HttpHarness.Run(output, 8, deadline.Token);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "http.json"), deadline.Token));
        var root = report.RootElement;
        Assert.Equal("passed", root.GetProperty("status").GetString());
        var scenarios = root.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(new[] { 1, 2, 4, 8 }, scenarios.Select(s => s.GetProperty("concurrency").GetInt32()));
        foreach (var scenario in scenarios)
        {
            Assert.Equal(8, scenario.GetProperty("total").GetInt32());
            Assert.Equal(8, scenario.GetProperty("outcomeCount").GetInt64());
            Assert.Equal(8, scenario.GetProperty("durationCount").GetInt64());
            Assert.True(scenario.GetProperty("budgetReleased").GetBoolean());
            var statuses = scenario.GetProperty("statusCounts");
            Assert.Single(statuses.EnumerateObject());
            Assert.Equal(8, statuses.GetProperty("200").GetInt32());
        }
    }

    private static HttpClient Client(byte[] body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new ResponseHandler(body, status)) { BaseAddress = new Uri("http://localhost/") };

    private sealed class ResponseHandler(byte[] body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
    }
}
