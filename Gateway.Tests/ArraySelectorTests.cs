using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace Gateway.Tests;

public sealed class ArraySelectorTests
{
    private const string Pan = "4111111111111111";
    private const string Masked = "************1111";

    private static Dictionary<string, string?> Configuration(string selector)
    {
        var values = PolicyConfiguration.Defaults();
        values["PanPolicies:Policies:0:Selector"] = selector;
        values["PanPolicies:CoverageContract:Required:0:Selector"] = selector;
        return values;
    }

    private static PanPolicyOptions Options(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build()
            .GetSection(PanPolicyOptions.SectionName).Get<PanPolicyOptions>()!;

    private static readonly PanPolicyValidator Validator = new([
        new RouteConfig { RouteId = "protected-payment" }, new RouteConfig { RouteId = "passthrough" }]);

    [Theory]
    [InlineData("objects")]
    [InlineData("payments")]
    [InlineData("strings")]
    [InlineData("nested")]
    [InlineData("root")]
    [InlineData("property-before-limit")]
    [InlineData("property-limit")]
    [InlineData("step-before-limit")]
    [InlineData("step-limit")]
    [InlineData("length-before-limit")]
    [InlineData("length-limit")]
    public void Valid_selectors_compile_with_unchanged_operational_ceilings(string scenario)
    {
        var (selector, tokens) = scenario switch
        {
            "objects" => ("$.payment.cardNumber", new[] { "payment", "cardNumber" }),
            "payments" => ("$.payments[*].cardNumber", new[] { "payments", "[*]", "cardNumber" }),
            "strings" => ("$.cards[*]", new[] { "cards", "[*]" }),
            "nested" => ("$.groups[*][*].cardNumber", new[] { "groups", "[*]", "[*]", "cardNumber" }),
            "root" => ("$[*][*]", new[] { "[*]", "[*]" }),
            "property-before-limit" => ("$." + new string('a', 63), new[] { new string('a', 63) }),
            "property-limit" => ("$." + new string('a', 64), new[] { new string('a', 64) }),
            "step-before-limit" => ("$.a" + string.Concat(Enumerable.Repeat("[*]", 14)),
                new[] { "a" }.Concat(Enumerable.Repeat("[*]", 14)).ToArray()),
            "step-limit" => ("$.a" + string.Concat(Enumerable.Repeat("[*]", 15)),
                new[] { "a" }.Concat(Enumerable.Repeat("[*]", 15)).ToArray()),
            "length-before-limit" => ("$" + string.Concat(Enumerable.Repeat("." + new string('a', 64), 15)) + "." + new string('a', 63),
                Enumerable.Repeat(new string('a', 64), 15).Append(new string('a', 63)).ToArray()),
            "length-limit" => ("$" + string.Concat(Enumerable.Repeat("." + new string('a', 64), 16)),
                Enumerable.Repeat(new string('a', 64), 16).ToArray()),
            _ => throw new ArgumentException(nameof(scenario))
        };
        var options = Options(Configuration(selector));
        Assert.True(Validator.Validate(null, options).Succeeded);
        var compiled = new PanPolicyCatalog(Microsoft.Extensions.Options.Options.Create(options)).Find("protected-payment")!;
        Assert.Equal(tokens, compiled.Segments);
        Assert.Equal(1048576, compiled.MaxBodyBytes);
        Assert.Equal("v1", compiled.Version);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("filter")]
    [InlineData("recursive")]
    [InlineData("property-wildcard")]
    [InlineData("quoted")]
    [InlineData("empty-brackets")]
    [InlineData("dot-wildcard")]
    [InlineData("missing-dot")]
    [InlineData("trailing-dot")]
    [InlineData("space")]
    [InlineData("newline")]
    [InlineData("bare-root")]
    [InlineData("property-over-limit")]
    [InlineData("step-over-limit")]
    [InlineData("object-step-over-limit")]
    [InlineData("length-over-limit")]
    public void Invalid_selectors_are_rejected_in_policy_and_coverage(string scenario)
    {
        var selector = scenario switch
        {
            "index" => "$.a[0]",
            "filter" => "$.a[?(@.x)]",
            "recursive" => "$..a[*]",
            "property-wildcard" => "$.a.*",
            "quoted" => "$.a['b']",
            "empty-brackets" => "$.a[]",
            "dot-wildcard" => "$.a.[*]",
            "missing-dot" => "$.a[*]b",
            "trailing-dot" => "$.a[*].",
            "space" => "$.a[ * ]",
            "newline" => "$.a[*]\n",
            "bare-root" => "$",
            "property-over-limit" => "$." + new string('a', 65),
            "step-over-limit" => "$.a" + string.Concat(Enumerable.Repeat("[*]", 16)),
            "object-step-over-limit" => "$" + string.Concat(Enumerable.Repeat(".a", 17)),
            "length-over-limit" => "$" + string.Concat(Enumerable.Repeat("." + new string('a', 64), 16)) + ".",
            _ => throw new ArgumentException(nameof(scenario))
        };
        var invalidPolicy = Options(Configuration(selector));
        Assert.True(Validator.Validate(null, invalidPolicy).Failed);
        var invalidCoverage = Options(Configuration("$.a[*]"));
        invalidCoverage.CoverageContract!.Required[0].Selector = selector;
        var result = Validator.Validate(null, invalidCoverage);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.StartsWith("Invalid PAN coverage requirement", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("without-wildcard")]
    [InlineData("different-array-depth")]
    [InlineData("case")]
    [InlineData("route")]
    public void Array_coverage_still_requires_exact_route_and_selector(string scenario)
    {
        var options = Options(Configuration("$.payments[*].cardNumber"));
        var requirement = options.CoverageContract!.Required[0];
        if (scenario == "route") requirement.RouteId = "passthrough";
        else requirement.Selector = scenario switch
        {
            "without-wildcard" => "$.payments.cardNumber",
            "different-array-depth" => "$.payments[*][*].cardNumber",
            "case" => "$.Payments[*].cardNumber",
            _ => throw new ArgumentException(nameof(scenario))
        };
        var result = Validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains("PAN policies do not satisfy the minimum coverage contract.", result.Failures!);
    }

    [Theory]
    [InlineData("mixed")]
    [InlineData("nested")]
    [InlineData("strings")]
    [InlineData("root")]
    [InlineData("objects")]
    [InlineData("duplicates")]
    public async Task Real_yarp_masks_every_match_and_preserves_unselected_structure(string scenario)
    {
        var (selector, input, expected) = scenario switch
        {
            "mixed" => ("$.payments[*].cardNumber",
                """{"payments":[{"cardNumber":"4111111111111111","other":"4111111111111111"},null,5,"plain",[],{}, {"CardNumber":"4111111111111111"},{"cardNumber":null},{"cardNumber":"4111-1111-1111-1111"}],"Payments":[{"cardNumber":"4111111111111111"}]}""",
                """{"payments":[{"cardNumber":"************1111","other":"4111111111111111"},null,5,"plain",[],{}, {"CardNumber":"4111111111111111"},{"cardNumber":null},{"cardNumber":"****-****-****-1111"}],"Payments":[{"cardNumber":"4111111111111111"}]}"""),
            "nested" => ("$.groups[*][*].cardNumber",
                """{"groups":[[{"cardNumber":"4111111111111111"},{"cardNumber":null}],[],null,{},[{"cardNumber":"5555555555554444"}]],"outside":"4111111111111111"}""",
                """{"groups":[[{"cardNumber":"************1111"},{"cardNumber":null}],[],null,{},[{"cardNumber":"************4444"}]],"outside":"4111111111111111"}"""),
            "strings" => ("$.cards[*]", """{"cards":["4111111111111111",null,"1234","bad-value","5555555555554444"],"other":["4111111111111111"]}""",
                """{"cards":["************1111",null,"[REDACTED]","[REDACTED]","************4444"],"other":["4111111111111111"]}"""),
            "root" => ("$[*][*]", """[["4111111111111111",null],[],null,["5555555555554444"]]""",
                """[["************1111",null],[],null,["************4444"]]"""),
            "objects" => ("$.payment.cardNumber", """{"payment":{"cardNumber":"4111111111111111"},"other":"4111111111111111"}""",
                """{"payment":{"cardNumber":"************1111"},"other":"4111111111111111"}"""),
            "duplicates" => ("$.payments[*].cardNumber",
                """{"payments":[{"cardNumber":"4111111111111111","cardNumber":"5555555555554444"}],"payments":[{"cardNumber":"4111111111111111"}]}""",
                """{"payments":[{"cardNumber":"************1111","cardNumber":"************4444"}],"payments":[{"cardNumber":"************1111"}]}"""),
            _ => throw new ArgumentException(nameof(scenario))
        };
        await using var hosts = await Hosts.Start(selector, input);
        using var response = await hosts.Client.GetAsync("/protected/arrays");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = await response.Content.ReadAsStringAsync();
        // Exact serialized text retains duplicate properties too; tree comparison checks other cases.
        if (scenario == "duplicates") Assert.Equal(expected, actual);
        else Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)));
        Assert.Equal(Encoding.UTF8.GetByteCount(actual), response.Content.Headers.ContentLength);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("case")]
    [InlineData("not-array")]
    [InlineData("not-object")]
    [InlineData("null-terminal")]
    [InlineData("no-implicit-array")]
    public async Task No_match_or_null_preserves_original_bytes(string scenario)
    {
        var (selector, body) = scenario switch
        {
            "empty" => ("$.cards[*]", "{ \"cards\" : [] }\n"),
            "missing" => ("$.cards[*]", "{ \"other\" : [\"4111111111111111\"] }\n"),
            "case" => ("$.cards[*]", "{ \"Cards\" : [\"4111111111111111\"] }\n"),
            "not-array" => ("$.cards[*]", "{ \"cards\" : {\"cardNumber\":\"4111111111111111\"} }\n"),
            "not-object" => ("$.cards[*].cardNumber", "{ \"cards\" : [null,5,\"4111111111111111\",[]] }\n"),
            "null-terminal" => ("$.cards[*]", "{ \"cards\" : [null,null] }\n"),
            "no-implicit-array" => ("$.cards.cardNumber", "{ \"cards\" : [{\"cardNumber\":\"4111111111111111\"}] }\n"),
            _ => throw new ArgumentException(nameof(scenario))
        };
        await using var hosts = await Hosts.Start(selector, body);
        Assert.Equal(body, await hosts.Client.GetStringAsync("/protected/arrays"));
    }

    [Theory]
    [InlineData("number")]
    [InlineData("boolean")]
    [InlineData("object")]
    [InlineData("array")]
    [InlineData("nested-object")]
    [InlineData("property-object")]
    public async Task Invalid_late_terminal_blocks_entire_body_without_partial_transformation(string scenario)
    {
        var invalid = scenario switch
        {
            "number" => "4111111111111111",
            "boolean" => "true",
            "object" or "nested-object" or "property-object" => "{\"secret\":\"4111111111111111\"}",
            "array" => "[\"4111111111111111\"]",
            _ => throw new ArgumentException(nameof(scenario))
        };
        var nested = scenario == "nested-object";
        var body = nested ? $"{{\"cards\":[[\"{Pan}\"],[{invalid}]]}}" : $"{{\"cards\":[\"{Pan}\",null,{invalid}]}}";
        var selector = nested ? "$.cards[*][*]" : "$.cards[*]";
        if (scenario == "property-object")
        {
            selector = "$.cards[*].cardNumber";
            body = $"{{\"cards\":[{{\"cardNumber\":\"{Pan}\"}},{{\"cardNumber\":{invalid}}}]}}";
        }
        await using var hosts = await Hosts.Start(selector, body);
        using var response = await hosts.Client.GetAsync("/protected/arrays");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var actual = await response.Content.ReadAsStringAsync();
        Assert.Equal("{\"code\":\"privacy_inspection_failed\"}", actual);
        Assert.DoesNotContain(Pan, actual);
        Assert.DoesNotContain(Masked, actual);
        Assert.False(response.Headers.Contains("X-Sensitive-Upstream"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Array_traversal_observes_cancellation_and_deadline_before_late_invalid_element(bool expire)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new TraversalClock(expire, cancellation);
        var options = Options(Configuration("$.cards[*]"));
        options.InspectionBudget!.InspectionTimeoutSeconds = 1;
        var budget = new InspectionBudget(Microsoft.Extensions.Options.Options.Create(options));
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = new PanPolicyCatalog(Microsoft.Extensions.Options.Options.Create(options)).Find("protected-payment")!;
        using var output = new MemoryStream();
        var http = new DefaultHttpContext();
        http.Response.Body = output;
        var body = "{\"cards\":[" + string.Concat(Enumerable.Repeat($"\"{Pan}\",", 2000)) + "42]}";
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        var context = new ResponseTransformContext { HttpContext = http, ProxyResponse = upstream, CancellationToken = cancellation.Token };
        var transform = new PanResponseTransform(NullLogger<PanResponseTransform>.Instance, telemetry, clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transform.ApplyAsync(context, policy).AsTask());
        Assert.Empty(output.ToArray());
        Assert.True(context.SuppressResponseBody);
        Assert.InRange(clock.Observations, 100, 150);
        Assert.Equal(0, budget.Snapshot.ActiveInspections);
        Assert.Equal(0, budget.Snapshot.ReservedBodyBytes);
    }

    // No real timer delivery: exercise monotonic checkpoints during CPU traversal, not wall-clock races.
    private sealed class TraversalClock(bool expire, CancellationTokenSource cancellation) : TimeProvider
    {
        public int Observations;
        public override long TimestampFrequency => 100;
        public override long GetTimestamp()
        {
            var observed = ++Observations;
            if (!expire && observed == 100) cancellation.Cancel();
            return expire ? observed : 0;
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new SilentTimer();
        private sealed class SilentTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Hosts(WebApplication api, WebApplication gateway) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new(new SocketsHttpHandler { UseProxy = false })
        { BaseAddress = new Uri(Address(gateway)), Timeout = TimeSpan.FromSeconds(15) };

        public static async Task<Hosts> Start(string selector, string body)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var api = builder.Build();
            api.MapGet("/arrays", async context =>
            {
                context.Response.ContentType = "application/json";
                context.Response.Headers["X-Sensitive-Upstream"] = Pan;
                await context.Response.WriteAsync(body);
            });
            await api.StartAsync();
            WebApplication? gateway = null;
            try
            {
                gateway = GatewayHost.Build([], Address(api) + "/", b =>
                {
                    b.Configuration.Sources.Clear();
                    b.Configuration.AddInMemoryCollection(Configuration(selector));
                    b.WebHost.UseUrls("http://127.0.0.1:0");
                });
                await gateway.StartAsync();
                return new Hosts(api, gateway);
            }
            catch
            {
                if (gateway is not null) await gateway.DisposeAsync();
                await api.DisposeAsync();
                throw;
            }
        }

        private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await gateway.DisposeAsync();
            await api.DisposeAsync();
        }
    }
}
