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
using Microsoft.Extensions.Options;
using Xunit;

namespace Gateway.Tests;

public sealed class BlockPolicyTests
{
    private const string PolicyError = "{\"code\":\"privacy_response_blocked\"}";
    private const string InspectionError = "{\"code\":\"privacy_inspection_failed\"}";
    private const string Pan = "4111111111111111";

    private static Dictionary<string, string?> Configuration(string selector, string? mode = "Block")
    {
        var values = PolicyConfiguration.Defaults();
        values["PanPolicies:Policies:0:Mode"] = mode;
        values["PanPolicies:Policies:0:Selector"] = selector;
        values["PanPolicies:CoverageContract:Required:0:Selector"] = selector;
        return values;
    }

    [Theory]
    [InlineData("Mask")]
    [InlineData("Block")]
    [InlineData("Observe")]
    public async Task Exact_modes_compile_into_immutable_catalog(string mode)
    {
        await using var app = GatewayHost.Build([], configure: b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(Configuration("$.cards[*]", mode));
            b.WebHost.UseUrls("http://127.0.0.1:0");
        });
        await app.StartAsync();
        var catalog = app.Services.GetRequiredService<PanPolicyCatalog>();
        var policy = catalog.Find("protected-payment")!;
        Assert.Equal(mode, policy.Mode);
        app.Configuration["PanPolicies:Policies:0:Mode"] = mode == "Block" ? "Mask" : "Block";
        ((IConfigurationRoot)app.Configuration).Reload();
        var options = app.Services.GetRequiredService<IOptions<PanPolicyOptions>>().Value;
        options.Policies[0].Mode = "Observe";
        options.Policies[0].Selector = "$.other";
        options.Policies.Clear();
        Assert.Same(policy, catalog.Find("protected-payment"));
        Assert.Equal(mode, policy.Mode);
        Assert.Equal(new[] { "cards", "[*]" }, policy.Segments);
        Assert.Equal("v1", policy.Version);
        Assert.Equal(1048576, policy.MaxBodyBytes);
    }

    [Theory]
    [InlineData("lower")]
    [InlineData("upper")]
    [InlineData("leading")]
    [InlineData("trailing")]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("numeric")]
    [InlineData("observe")]
    [InlineData("newline")]
    public void Invalid_modes_prevent_build(string scenario)
    {
        var mode = scenario switch
        {
            "lower" => "block", "upper" => "BLOCK", "leading" => " Block", "trailing" => "Block ",
            "empty" => "", "missing" => null, "numeric" => "2", "observe" => "observe", "newline" => "Block\n",
            _ => throw new ArgumentException(nameof(scenario))
        };
        var failure = Assert.Throws<InvalidOperationException>(() => GatewayHost.Build([], configure: b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(Configuration("$.cards[*]", mode));
        }));
        Assert.IsType<OptionsValidationException>(failure.InnerException);
    }

    [Fact]
    public void Positional_compiled_policy_retains_Mask_default_and_deconstruct_contract()
    {
        var policy = new CompiledPanPolicy("PAN", "v1", "protected-payment", ["cards", "[*]"], 1024);
        var (id, version, route, segments, limit) = policy;
        Assert.Equal("Mask", policy.Mode);
        Assert.Equal("PAN", id);
        Assert.Equal("v1", version);
        Assert.Equal("protected-payment", route);
        Assert.Equal(new[] { "cards", "[*]" }, segments);
        Assert.Equal(1024, limit);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("empty")]
    [InlineData("short")]
    [InlineData("incompatible")]
    [InlineData("late")]
    [InlineData("root")]
    [InlineData("nested")]
    [InlineData("duplicate-first")]
    [InlineData("duplicate-last")]
    public async Task Real_yarp_blocks_any_selected_string_without_upstream_data(string scenario)
    {
        var (selector, body) = scenario switch
        {
            "normal" => ("$.payment.cardNumber", "{\"payment\":{\"cardNumber\":\"4111111111111111\"}}"),
            "empty" => ("$.cards[*]", "{\"cards\":[\"\"]}"),
            "short" => ("$.cards[*]", "{\"cards\":[\"1\"]}"),
            "incompatible" => ("$.cards[*]", "{\"cards\":[\"not-a-PAN ☃\"]}"),
            "late" => ("$.cards[*].pan", "{\"cards\":[{},null,5,[],{\"pan\":null},{\"pan\":\"late\"}]}"),
            "root" => ("$[*]", "[null,\"root\"]"),
            "nested" => ("$.cards[*][*]", "{\"cards\":[[],[null],[\"nested\"]]}"),
            "duplicate-first" => ("$.cards[*]", "{\"cards\":[\"first\"],\"cards\":[null]}"),
            "duplicate-last" => ("$.cards[*]", "{\"cards\":[null],\"cards\":[\"last\"]}"),
            _ => throw new ArgumentException(nameof(scenario))
        };
        await using var hosts = await Hosts.Start(selector, body);
        using var response = await hosts.Client.GetAsync("/protected/test");
        await AssertError(response, PolicyError);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (hosts.Admission.ActiveRequests != 0)
        {
            cleanup.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
        Assert.Equal((0, 0L), hosts.Budget.Snapshot);
        Assert.Equal(0, hosts.Admission.ActiveRequests);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("case")]
    [InlineData("nulls")]
    [InlineData("empty-array")]
    [InlineData("not-array")]
    [InlineData("not-object")]
    [InlineData("root-null")]
    public async Task Real_yarp_no_match_or_null_preserves_exact_bytes_and_status(string scenario)
    {
        var (selector, body) = scenario switch
        {
            "missing" => ("$.cards[*]", "{ \"other\" : \"4111111111111111\" }\r\n"),
            "case" => ("$.cards[*]", "{ \"Cards\" : [\"4111111111111111\"] }\n"),
            "nulls" => ("$.cards[*]", "{ \"cards\" : [null, null], \"cards\" : [null] }\n"),
            "empty-array" => ("$.cards[*]", "{ \"cards\" : [] }\n"),
            "not-array" => ("$.cards[*]", "{ \"cards\" : \"4111111111111111\" }\n"),
            "not-object" => ("$.cards[*].pan", "{ \"cards\" : [null, 42, [], \"plain\"] }\n"),
            "root-null" => ("$[*]", " null \n"),
            _ => throw new ArgumentException(nameof(scenario))
        };
        await using var hosts = await Hosts.Start(selector, body);
        using var response = await hosts.Client.GetAsync("/protected/test");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(Encoding.UTF8.GetBytes(body), await response.Content.ReadAsByteArrayAsync());
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData("number-early")]
    [InlineData("number-late")]
    [InlineData("bool-late")]
    [InlineData("object-late")]
    [InlineData("array-late")]
    [InlineData("duplicate-late")]
    [InlineData("duplicate-early")]
    [InlineData("json-early")]
    [InlineData("json-late")]
    public async Task Real_yarp_invalid_terminal_or_JSON_wins_over_policy_match(string scenario)
    {
        var body = scenario switch
        {
            "number-early" => "{\"cards\":[42,\"selected\"]}",
            "number-late" => "{\"cards\":[\"selected\",42]}",
            "bool-late" => "{\"cards\":[\"selected\",true]}",
            "object-late" => "{\"cards\":[\"selected\",{}]}",
            "array-late" => "{\"cards\":[\"selected\",[]]}",
            "duplicate-late" => "{\"cards\":[\"selected\"],\"cards\":[42]}",
            "duplicate-early" => "{\"cards\":[42],\"cards\":[\"selected\"]}",
            "json-early" => "INVALID {\"cards\":[\"selected\"]}",
            "json-late" => "{\"cards\":[\"selected\"],\"tail\":INVALID}",
            _ => throw new ArgumentException(nameof(scenario))
        };
        await using var hosts = await Hosts.Start("$.cards[*]", body);
        using var response = await hosts.Client.GetAsync("/protected/test");
        await AssertError(response, InspectionError);
    }

    [Fact]
    public async Task Real_yarp_waits_for_complete_capture_before_returning_late_inspection_failure()
    {
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hosts = await Hosts.Start("$.cards[*]", "", handler: async context =>
        {
            await context.Response.WriteAsync("{\"cards\":[\"4111111111111111\"]");
            await context.Response.Body.FlushAsync();
            flushed.TrySetResult();
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync(",\"late\":INVALID}");
        });
        var pending = hosts.Client.GetAsync("/protected/test", HttpCompletionOption.ResponseHeadersRead);
        try
        {
            await flushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Upstream flush precedes gateway scheduling; observe lease acquisition, not elapsed time.
            using var admitted = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (hosts.Budget.Snapshot.ActiveInspections != 1)
            {
                admitted.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            Assert.False(pending.IsCompleted);
            Assert.Equal(1, hosts.Budget.Snapshot.ActiveInspections);
            Assert.Equal(1, hosts.Admission.ActiveRequests);
        }
        finally { release.TrySetResult(); }
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertError(response, InspectionError);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("204")]
    [InlineData("304")]
    [InlineData("text")]
    [InlineData("suffix-json")]
    public async Task Real_yarp_excluded_responses_keep_existing_semantics(string scenario)
    {
        var status = scenario == "204" ? 204 : scenario == "304" ? 304 : 422;
        var type = scenario == "text" ? "text/plain" : scenario == "suffix-json" ? "application/problem+json" : "application/json";
        const string body = "{\"cards\":[\"4111111111111111\"]}";
        await using var hosts = await Hosts.Start("$.cards[*]", body, status, type);
        using var request = new HttpRequestMessage(scenario == "HEAD" ? HttpMethod.Head : HttpMethod.Get, "/protected/test");
        using var response = await hosts.Client.SendAsync(request);
        if (scenario == "304") await AssertError(response, InspectionError);
        else
        {
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal(scenario is "HEAD" or "204" ? "" : body, await response.Content.ReadAsStringAsync());
        }
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal((0, 0L), hosts.Budget.Snapshot);
    }

    private static async Task AssertError(HttpResponseMessage response, string expected)
    {
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetBytes(expected), bytes);
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.True(response.Headers.CacheControl!.NoStore);
        foreach (var header in new[] { "X-Sensitive-Upstream", "Set-Cookie", "ETag", "Last-Modified", "Digest", "Content-Language" })
        {
            Assert.DoesNotContain(response.Headers.Concat(response.Content.Headers),
                item => string.Equals(item.Key, header, StringComparison.OrdinalIgnoreCase));
        }
        Assert.DoesNotContain(Pan, Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("************1111", Encoding.UTF8.GetString(bytes));
    }

    private sealed class Hosts(WebApplication api, WebApplication gateway) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new(new SocketsHttpHandler { UseProxy = false })
        { BaseAddress = new Uri(Address(gateway)), Timeout = TimeSpan.FromSeconds(15) };
        public InspectionBudget Budget => gateway.Services.GetRequiredService<InspectionBudget>();
        public ProxyAdmission Admission => gateway.Services.GetRequiredService<ProxyAdmission>();

        public static async Task<Hosts> Start(string selector, string body, int status = 422,
            string type = "application/json", Func<HttpContext, Task>? handler = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var api = builder.Build();
            api.Map("/{**path}", async context =>
            {
                context.Response.StatusCode = status;
                context.Response.ContentType = type;
                context.Response.Headers["X-Sensitive-Upstream"] = Pan;
                context.Response.Headers.SetCookie = "synthetic=" + Pan;
                context.Response.Headers.ETag = "\"" + Pan + "\"";
                context.Response.Headers["Digest"] = Pan;
                context.Response.Headers.ContentLanguage = "synthetic";
                context.Response.Headers.LastModified = "Wed, 01 Jan 2025 00:00:00 GMT";
                context.Response.Headers.CacheControl = "public, max-age=86400";
                if (HttpMethods.IsHead(context.Request.Method) || status is 204 or 304) return;
                if (handler is not null) await handler(context);
                else await context.Response.WriteAsync(body);
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
                return new(api, gateway);
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
