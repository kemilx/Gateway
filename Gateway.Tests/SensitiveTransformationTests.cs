using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Yarp.ReverseProxy.Transforms;

namespace Gateway.Tests;

public sealed class SensitiveTransformationTests
{
    private const string InspectionError = "{\"code\":\"privacy_inspection_failed\"}";
    private const string BlockError = "{\"code\":\"privacy_response_blocked\"}";
    private static Dictionary<string, string?> Configuration(string? transformation, string selector = "$.values[*]", string mode = "Mask")
    {
        var values = PolicyConfiguration.Defaults();
        values["PanPolicies:Policies:0:Transformation"] = transformation;
        // Deliberately unrelated to the chosen transformation, including a misleading PAN id.
        values["PanPolicies:Policies:0:PolicyId"] = "PAN";
        values["PanPolicies:Policies:0:Mode"] = mode;
        values["PanPolicies:Policies:0:Selector"] = selector;
        values["PanPolicies:CoverageContract:Required:0:Selector"] = selector;
        return values;
    }

    [Theory]
    [InlineData("explicit-null")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("lower-email")]
    [InlineData("upper-email")]
    [InlineData("lower-phone")]
    [InlineData("lower-pan")]
    [InlineData("trailing-space")]
    [InlineData("leading-space")]
    [InlineData("newline")]
    [InlineData("numeric")]
    [InlineData("unknown")]
    public void Invalid_transformations_prevent_build(string scenario)
    {
        var transformation = scenario switch
        {
            "explicit-null" => null, "empty" => "", "whitespace" => " ",
            "lower-email" => "email", "upper-email" => "EMAIL", "lower-phone" => "phone", "lower-pan" => "pan",
            "trailing-space" => "Email ", "leading-space" => " Phone", "newline" => "PAN\n",
            "numeric" => "0", "unknown" => "Observe", _ => throw new ArgumentException(nameof(scenario))
        };
        var failure = Assert.Throws<InvalidOperationException>(() => GatewayHost.Build([], configure: b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(Configuration(transformation));
        }));
        Assert.IsType<OptionsValidationException>(failure.InnerException);
    }

    [Theory]
    [InlineData("PAN")]
    [InlineData("Email")]
    [InlineData("Phone")]
    [InlineData("missing")]
    public async Task Transformation_defaults_contract_and_catalog_are_stable(string transformation)
    {
        var positional = new CompiledPanPolicy("arbitrary", "v1", "route", ["value"], 1024);
        var (id, version, route, segments, limit) = positional;
        Assert.Equal(("arbitrary", "v1", "route", 1024), (id, version, route, limit));
        Assert.Equal(new[] { "value" }, segments);
        Assert.Equal("PAN", positional.Transformation);
        Assert.Equal("Mask", positional.Mode);
        Assert.Equal("PAN", new PanPolicyDefinition().Transformation);
        Assert.Equal("PanPolicies", PanPolicyOptions.SectionName);
        var values = Configuration(transformation);
        if (transformation == "missing") values.Remove("PanPolicies:Policies:0:Transformation");
        await using var app = GatewayHost.Build([], configure: b =>
        {
            b.Configuration.Sources.Clear();
            b.Configuration.AddInMemoryCollection(values);
            b.WebHost.UseUrls("http://127.0.0.1:0");
        });
        await app.StartAsync();
        var expected = transformation == "missing" ? "PAN" : transformation;
        var catalog = app.Services.GetRequiredService<PanPolicyCatalog>();
        var policy = catalog.Find("protected-payment")!;
        Assert.Equal(expected, policy.Transformation);
        Assert.Equal("Mask", policy.Mode);
        app.Configuration["PanPolicies:Policies:0:Transformation"] = "Phone";
        ((IConfigurationRoot)app.Configuration).Reload();
        app.Services.GetRequiredService<IOptions<PanPolicyOptions>>().Value.Policies[0].Transformation = "Email";
        Assert.Same(policy, catalog.Find("protected-payment"));
        Assert.Equal(expected, policy.Transformation);
        using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };
        using var summary = JsonDocument.Parse(await client.GetStringAsync("/dashboard/api/summary"));
        Assert.Equal(1, summary.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(expected, summary.RootElement.GetProperty("policies")[0].GetProperty("transformation").GetString());
        await app.StopAsync();
    }

    [Fact]
    public async Task Email_formats_and_boundaries()
    {
        var domain189 = new string('a', 63) + "." + new string('b', 63) + "." + new string('c', 61);
        var cases = new (string Input, string? Domain)[]
        {
            ("user@example.test", "example.test"), ("a@X", "X"), ("a.b@Ex-Ample.Test", "Ex-Ample.Test"),
            ("!#$%&'*+-/=?^_`{|}~@xn--example.test", "xn--example.test"),
            (new string('a', 64) + "@x", "x"), (new string('a', 65) + "@x", null),
            (new string('a', 64) + "@" + domain189, domain189),
            (new string('a', 64) + "@" + domain189 + "c", null),
            ("a@" + new string('b', 63), new string('b', 63)), ("a@" + new string('b', 64), null),
            ("", null), ("a", null), ("@x", null), ("a@", null), ("a@@x", null),
            (".a@x", null), ("a.@x", null), ("a..b@x", null), ("a@.x", null), ("a@x.", null),
            ("a@x..y", null), ("a@-x", null), ("a@x-", null), ("a@x_y", null),
            ("\"a\"@x", null), ("Name <a@x>", null), ("a@[127.0.0.1]", null),
            ("é@x", null), ("a@é.test", null), ("a b@x", null), ("a@x y", null),
            ("a@x\n", null), (" a@x", null), ("a\t@x", null), ("a\0@x", null), ("１２@x", null),
            ("a@x1.test", "x1.test"), ("a@123", "123"), ("a@127.0.0.1", "127.0.0.1"),
            ("a@x\r", null), ("a@x\r\n", null)
        };
        Assert.Equal(254, cases[6].Input.Length);
        Assert.Equal(255, cases[7].Input.Length);
        foreach (var (input, domain) in cases)
        {
            var result = await Transform("Email", JsonSerializer.Serialize(new { values = new[] { input } }));
            using var json = JsonDocument.Parse(result.Body);
            Assert.Equal(domain is null ? "[REDACTED]" : "***@" + domain, json.RootElement.GetProperty("values")[0].GetString());
            Assert.Equal(200, result.Status);
            Assert.Equal(1, result.Metrics.ValidatedMasks);
        }
    }

    [Fact]
    public async Task Phone_formats_and_boundaries()
    {
        var cases = new (string Input, string Expected)[]
        {
            ("+1-809-555-0199", "+*-***-***-0199"), ("12345", "*2345"),
            ("123456789012345", "***********2345"), ("1 2-345", "* 2-345"),
            ("+12345", "+*2345"), ("+ 12345", "+ *2345"),
            ("1" + new string(' ', 59) + "2345", "*" + new string(' ', 59) + "2345"),
            ("1" + new string(' ', 60) + "2345", "[REDACTED]"),
            ("", "[REDACTED]"), ("1", "[REDACTED]"), ("1234", "[REDACTED]"),
            ("1234567890123456", "[REDACTED]"), ("+", "[REDACTED]"), ("+1234", "[REDACTED]"),
            ("++12345", "[REDACTED]"), ("12+345", "[REDACTED]"), (" 12345", "[REDACTED]"),
            ("-12345", "[REDACTED]"), ("12345 ", "[REDACTED]"), ("12345-", "[REDACTED]"),
            ("(123)45", "[REDACTED]"), ("12345x1", "[REDACTED]"), ("１２３４５", "[REDACTED]"),
            ("١٢٣٤٥", "[REDACTED]"), ("123\t45", "[REDACTED]"), ("12345\n", "[REDACTED]"),
            ("123–45", "[REDACTED]"), ("123\u00a045", "[REDACTED]")
        };
        Assert.Equal(64, cases[6].Input.Length);
        Assert.Equal(65, cases[7].Input.Length);
        foreach (var (input, expected) in cases)
        {
            var result = await Transform("Phone", JsonSerializer.Serialize(new { values = new[] { input } }));
            using var json = JsonDocument.Parse(result.Body);
            Assert.Equal(expected, json.RootElement.GetProperty("values")[0].GetString());
            Assert.Equal(200, result.Status);
            Assert.Equal(1, result.Metrics.ValidatedMasks);
        }
    }

    [Theory]
    [InlineData("4111-1111 1111-1111", "****-**** ****-1111")]
    [InlineData("+12345", "[REDACTED]")]
    [InlineData("user@example.test", "[REDACTED]")]
    [InlineData("1234", "[REDACTED]")]
    [InlineData("１２３４５", "[REDACTED]")]
    public async Task PAN_default_mask_is_unchanged(string input, string expected)
    {
        var result = await Transform(null, JsonSerializer.Serialize(new { values = new[] { input } }));
        using var json = JsonDocument.Parse(result.Body);
        Assert.Equal(expected, json.RootElement.GetProperty("values")[0].GetString());
        Assert.Equal(1, result.Metrics.ValidatedMasks);
    }

    [Theory]
    [InlineData("Email", "u\\u0073er@example.test", "***@example.test")]
    [InlineData("Phone", "\\u002b1-809-555-0199", "+*-***-***-0199")]
    public async Task Selected_objects_arrays_duplicates_and_escapes_are_transformed(string transformation, string encoded, string expected)
    {
        var body = "{\"values\":[{\"v\":\"" + encoded + "\",\"v\":\"\",\"other\":\"é\"},null,42,[],{\"v\":null}],\"values\":[{\"v\":\"" + encoded + "\"}]}";
        var result = await Transform(transformation, body, segments: ["values", "[*]", "v"]);
        Assert.Equal(200, result.Status);
        Assert.Equal(3, result.Metrics.ValidatedMasks);
        using var json = JsonDocument.Parse(result.Body);
        var properties = json.RootElement.EnumerateObject().ToArray();
        Assert.Equal(2, properties.Length);
        var first = properties[0].Value[0].EnumerateObject().ToArray();
        Assert.Equal(new[] { "v", "v", "other" }, first.Select(p => p.Name));
        Assert.Equal(expected, first[0].Value.GetString());
        Assert.Equal("[REDACTED]", first[1].Value.GetString());
        Assert.Equal("é", first[2].Value.GetString());
        Assert.Equal(JsonValueKind.Null, properties[0].Value[4].GetProperty("v").ValueKind);
        Assert.Equal(expected, properties[1].Value[0].GetProperty("v").GetString());
    }

    [Theory]
    [InlineData("Email")]
    [InlineData("Phone")]
    public async Task No_match_and_null_preserve_bytes(string transformation)
    {
        foreach (var mode in new[] { "Mask", "Block" })
        foreach (var body in new[] { " { \"other\" : \"é\\u0041\" }\r\n", "{ \"values\" : [null,null], \"values\" : [] }\n", "{\"values\":\"not-an-array\"}", " null \n" })
        {
            var result = await Transform(transformation, body, mode);
            Assert.Equal(Encoding.UTF8.GetBytes(body), result.Body);
            Assert.Equal(200, result.Status);
            Assert.Equal(0, result.Metrics.ValidatedMasks);
            Assert.Equal(1, result.Metrics.Outcomes.Inspected);
        }
    }

    [Theory]
    [InlineData("Email", "a@x", "***@x")]
    [InlineData("Phone", "12345", "*2345")]
    public async Task Root_nested_arrays_and_escaped_property_names_use_the_same_transformation(string transformation, string input, string expected)
    {
        var nested = await Transform(transformation, "[[null],[\"" + input + "\"],[]]", segments: ["[*]", "[*]"]);
        using var arrays = JsonDocument.Parse(nested.Body);
        Assert.Equal(JsonValueKind.Null, arrays.RootElement[0][0].ValueKind);
        Assert.Equal(expected, arrays.RootElement[1][0].GetString());
        Assert.Equal(1, nested.Metrics.ValidatedMasks);
        var escaped = await Transform(transformation, "{\"\\u0076\":\"" + input + "\"}", segments: ["v"]);
        using var objectValue = JsonDocument.Parse(escaped.Body);
        Assert.Equal(expected, objectValue.RootElement.GetProperty("v").GetString());
        Assert.Equal(1, escaped.Metrics.ValidatedMasks);
    }

    [Theory]
    [InlineData("Email", "Mask")]
    [InlineData("Email", "Block")]
    [InlineData("Phone", "Mask")]
    [InlineData("Phone", "Block")]
    public async Task Real_HTTP_no_match_and_null_preserve_original_UTF8_bytes(string transformation, string mode)
    {
        const string body = " { \"values\" : [null], \"other\" : \"é\\u0041\" }\r\n";
        await using var hosts = await Hosts.Start(transformation, body, mode);
        using var response = await hosts.Client.GetAsync("/protected/test");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(Encoding.UTF8.GetBytes(body), bytes);
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(0, hosts.Telemetry.DashboardMetrics.ValidatedMasks);
        Assert.Equal(1, hosts.Telemetry.DashboardMetrics.Outcomes.Inspected);
        Assert.Equal(bytes.Length, hosts.Telemetry.DashboardMetrics.Output.TotalBytes);
    }

    [Theory]
    [InlineData("Email", "Mask")]
    [InlineData("Phone", "Mask")]
    [InlineData("Email", "Block")]
    [InlineData("Phone", "Block")]
    public async Task Late_invalid_terminal_discards_all_masks(string transformation, string mode)
    {
        foreach (var terminal in new[] { "42", "true", "{}", "[]" })
        {
            var result = await Transform(transformation, "{\"values\":[\"user@example.test\",\"+12345\"],\"values\":[" + terminal + "]}", mode);
            Assert.Equal(502, result.Status);
            Assert.Equal(InspectionError, Encoding.UTF8.GetString(result.Body));
            Assert.Equal(0, result.Metrics.ValidatedMasks);
            Assert.Equal(1, result.Metrics.Outcomes.Blocked);
            Assert.Equal(0, result.Metrics.Outcomes.PolicyBlocked);
        }
    }

    [Theory]
    [InlineData("Email")]
    [InlineData("Phone")]
    public async Task Block_ignores_format_but_validates_all_terminals(string transformation)
    {
        foreach (var input in new[] { "", "incompatible é", "a@x", "+12345" })
        {
            var result = await Transform(transformation, JsonSerializer.Serialize(new { values = new[] { input } }), "Block");
            Assert.Equal(502, result.Status);
            Assert.Equal(BlockError, Encoding.UTF8.GetString(result.Body));
            Assert.Equal(0, result.Metrics.ValidatedMasks);
            Assert.Equal(1, result.Metrics.Outcomes.PolicyBlocked);
        }
        var malformed = await Transform(transformation, "{\"values\":[\"a@x\"],\"tail\":INVALID}", "Block");
        Assert.Equal(InspectionError, Encoding.UTF8.GetString(malformed.Body));
        Assert.Equal(0, malformed.Metrics.ValidatedMasks);
        Assert.Equal(1, malformed.Metrics.Outcomes.Blocked);
    }

    [Theory]
    [InlineData("Email", "user@example.test", "***@example.test")]
    [InlineData("Phone", "+1-809-555-0199", "+*-***-***-0199")]
    public async Task Real_HTTP_preserves_contract_and_redacts_observability(string transformation, string input, string expected)
    {
        var body = "{\"values\":[\"" + input + "\",\"incompatible\",null],\"other\":\"é\"}";
        await using var hosts = await Hosts.Start(transformation, body);
        using var response = await hosts.Client.GetAsync("/protected/test");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(expected, json.RootElement.GetProperty("values")[0].GetString());
        Assert.Equal("[REDACTED]", json.RootElement.GetProperty("values")[1].GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("values")[2].ValueKind);
        Assert.Equal("é", json.RootElement.GetProperty("other").GetString());
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.False(response.Headers.Contains("ETag"));
        Assert.False(response.Headers.Contains("Digest"));
        Assert.False(response.Content.Headers.Contains("Last-Modified"));
        Assert.Equal("identity", hosts.AcceptEncoding);
        Assert.Equal(1, hosts.Contacts);
        await WaitForBudgetRelease(hosts.Budget);
        var summaryText = await hosts.Client.GetStringAsync("/dashboard/api/summary");
        using var summary = JsonDocument.Parse(summaryText);
        var policy = summary.RootElement.GetProperty("policies")[0];
        Assert.Equal(transformation, policy.GetProperty("transformation").GetString());
        Assert.Equal("PAN", policy.GetProperty("policyId").GetString());
        Assert.Equal(new[] { "maxBodyBytes", "mode", "policyId", "routeId", "ruleCount", "transformation", "transformations", "version" }, policy.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(1, policy.GetProperty("ruleCount").GetInt32());
        Assert.Equal(new[] { transformation }, policy.GetProperty("transformations").EnumerateArray().Select(value => value.GetString()));
        var metrics = summary.RootElement.GetProperty("metrics");
        Assert.Equal(2, metrics.GetProperty("validatedMasks").GetInt64());
        Assert.Equal(1, metrics.GetProperty("outcomes").GetProperty("inspected").GetInt64());
        Assert.Equal(Encoding.UTF8.GetByteCount(body), metrics.GetProperty("input").GetProperty("totalBytes").GetInt64());
        Assert.Equal(bytes.Length, metrics.GetProperty("output").GetProperty("totalBytes").GetInt64());
        Assert.Equal(1, metrics.GetProperty("input").GetProperty("samples").GetInt64());
        Assert.Equal(1, metrics.GetProperty("output").GetProperty("samples").GetInt64());
        var logs = string.Join('\n', hosts.Logs.Messages);
        Assert.Contains("Protected response inspected", logs);
        foreach (var text in new[] { input, expected, "incompatible", "$.values", body })
        {
            Assert.DoesNotContain(text, logs);
            Assert.DoesNotContain(text, summaryText);
        }
        Assert.Equal((0, 0L), hosts.Budget.Snapshot);
    }

    [Theory]
    [InlineData("Email", "Mask", false)]
    [InlineData("Phone", "Mask", false)]
    [InlineData("Email", "Block", false)]
    [InlineData("Phone", "Block", false)]
    [InlineData("Email", "Block", true)]
    [InlineData("Phone", "Mask", true)]
    public async Task Real_HTTP_Block_and_late_failure_return_only_fixed_error(string transformation, string mode, bool lateError)
    {
        var body = lateError ? "{\"values\":[\"user@example.test\",42]}" : "{\"values\":[\"\",\"+12345\",null]}";
        await using var hosts = await Hosts.Start(transformation, body, mode);
        using var response = await hosts.Client.GetAsync("/protected/test");
        Assert.Equal(lateError || mode == "Block" ? HttpStatusCode.BadGateway : HttpStatusCode.UnprocessableEntity, response.StatusCode);
        // Compare decoded JSON for successful strings because the JSON writer escapes plus.
        if (lateError || mode == "Block")
        {
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(lateError ? InspectionError : BlockError, Encoding.UTF8.GetString(bytes));
            Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.False(response.Headers.Contains("X-Upstream-Marker"));
            Assert.False(response.Headers.Contains("Set-Cookie"));
            Assert.False(response.Headers.Contains("ETag"));
            Assert.False(response.Headers.Contains("Digest"));
            Assert.False(response.Content.Headers.Contains("Last-Modified"));
        }
        else
        {
            using var actual = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("[REDACTED]", actual.RootElement.GetProperty("values")[0].GetString());
            Assert.Equal(transformation == "Phone" ? "+*2345" : "[REDACTED]", actual.RootElement.GetProperty("values")[1].GetString());
        }
        await WaitForBudgetRelease(hosts.Budget);
        var totals = hosts.Telemetry.DashboardMetrics;
        Assert.Equal(lateError ? 1 : 0, totals.Outcomes.Blocked);
        Assert.Equal(!lateError && mode == "Block" ? 1 : 0, totals.Outcomes.PolicyBlocked);
        Assert.Equal(!lateError && mode == "Mask" ? 2 : 0, totals.ValidatedMasks);
        Assert.Equal((0, 0L), hosts.Budget.Snapshot);
    }

    private static async Task WaitForBudgetRelease(InspectionBudget budget)
    {
        // Reading the client response can finish before server-side terminal publication
        // and lease disposal. Wait for the resource itself, not a telemetry outcome.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (budget.Snapshot != (0, 0L))
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private sealed record Result(int Status, byte[] Body, DashboardMetrics Metrics);
    private static async Task<Result> Transform(string? transformation, string body, string mode = "Mask", string[]? segments = null)
    {
        var budget = new InspectionBudget(Options.Create(new PanPolicyOptions { InspectionBudget = new() }));
        using var telemetry = new PrivacyTelemetry(budget);
        var policy = new CompiledPanPolicy("unrelated-id", "v1", "route", [.. segments ?? ["values", "[*]"]], 1048576)
        { Mode = mode };
        if (transformation is not null) policy = policy with { Transformation = transformation };
        using var output = new MemoryStream();
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Response.Body = output;
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        var context = new ResponseTransformContext { HttpContext = http, ProxyResponse = upstream, HeadersCopied = true };
        await new PanResponseTransform(NullLogger<PanResponseTransform>.Instance, telemetry).ApplyAsync(context, policy);
        Assert.True(context.SuppressResponseBody);
        Assert.Equal(output.Length, http.Response.ContentLength);
        Assert.Equal((0, 0L), budget.Snapshot);
        return new(http.Response.StatusCode, output.ToArray(), telemetry.DashboardMetrics);
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private sealed class CapturedLogs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Messages.Enqueue(formatter(state, exception));
        }
        public void Dispose() { }
    }

    private sealed class Hosts(WebApplication api, WebApplication gateway, CapturedLogs logs, Func<int> contacts, Func<string?> encoding) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new(new SocketsHttpHandler { UseProxy = false })
        { BaseAddress = new Uri(Address(gateway)), Timeout = TimeSpan.FromSeconds(10) };
        public CapturedLogs Logs => logs;
        public int Contacts => contacts();
        public string? AcceptEncoding => encoding();
        public InspectionBudget Budget => gateway.Services.GetRequiredService<InspectionBudget>();
        public PrivacyTelemetry Telemetry => gateway.Services.GetRequiredService<PrivacyTelemetry>();
        public static async Task<Hosts> Start(string transformation, string body, string mode = "Mask")
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var api = builder.Build();
            var contacts = 0;
            string? encoding = null;
            api.MapGet("/test", async context =>
            {
                Interlocked.Increment(ref contacts);
                encoding = context.Request.Headers.AcceptEncoding;
                context.Response.StatusCode = 422;
                context.Response.ContentType = "application/json";
                context.Response.Headers.ETag = "\"synthetic\"";
                context.Response.Headers.LastModified = "Wed, 01 Jan 2025 00:00:00 GMT";
                context.Response.Headers["Digest"] = "synthetic";
                context.Response.Headers["X-Upstream-Marker"] = "synthetic";
                context.Response.Headers.SetCookie = "synthetic=1";
                await context.Response.WriteAsync(body);
            });
            await api.StartAsync();
            WebApplication? gateway = null;
            try
            {
                var logs = new CapturedLogs();
                gateway = GatewayHost.Build([], Address(api) + "/", b =>
                {
                    b.Configuration.Sources.Clear();
                    b.Configuration.AddInMemoryCollection(Configuration(transformation, mode: mode));
                    b.WebHost.UseUrls("http://127.0.0.1:0");
                    b.Logging.ClearProviders();
                    b.Logging.AddProvider(logs);
                });
                await gateway.StartAsync();
                return new(api, gateway, logs, () => Volatile.Read(ref contacts), () => encoding);
            }
            catch
            {
                if (gateway is not null) await gateway.DisposeAsync();
                await api.DisposeAsync();
                throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await gateway.DisposeAsync();
            await api.DisposeAsync();
        }
    }
}
