using System.Net;
using System.Text.RegularExpressions;
using Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gateway.Tests;

public sealed class DashboardPresentationTests
{
    private static WebApplication Build() => GatewayHost.Build([], configure: builder =>
    {
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(PolicyConfiguration.Defaults());
        builder.WebHost.UseUrls("http://127.0.0.1:0");
    });

    private static HttpClient Client(WebApplication app) => new()
    {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
        Timeout = TimeSpan.FromSeconds(10)
    };

    [Fact]
    public async Task Dashboard_html_preserves_script_control_ids_and_avoids_old_milestone_brand()
    {
        await using var app = Build();
        await app.StartAsync();
        using var client = Client(app);
        var html = await client.GetStringAsync("/dashboard");

        foreach (var id in new[] { "summary", "refresh", "auto-refresh", "request-state", "announcement",
            "error-notice", "error-detail", "updated-at", "live", "ready", "started-at", "active", "reserved",
            "masks", "outcomes", "policies", "input-total", "input-samples", "output-total", "output-samples",
            "duration-mean", "duration-samples", "duration-total", "reload-status", "reload-dates",
            "reload-checked-at", "reload-applied-at", "reload-rejection", "reload-reason", "demo-mask",
            "demo-block", "demo-run", "demo-result" })
            Assert.Single(Regex.Matches(html, """\bid\s*=\s*['"]""" + Regex.Escape(id) + """['"]""").Cast<Match>());

        Assert.DoesNotContain("MVP24", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dashboard_discloses_observational_and_synthetic_scope_and_metric_limits()
    {
        await using var app = Build();
        await app.StartAsync();
        using var client = Client(app);
        var html = await client.GetStringAsync("/dashboard");
        foreach (var disclosure in new[] { "datos sintéticos", "solo consulta", "no aprobado para producción",
            "Observe entrega los datos originales", "reserva nominal, no techo de RAM",
            "no representan hallazgos PAN ni entrega efectiva", "ni envía solicitudes a ninguna API" })
            Assert.Contains(disclosure, html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dashboard_provides_skip_target_live_announcements_and_named_table_regions()
    {
        await using var app = Build();
        await app.StartAsync();
        using var client = Client(app);
        var html = await client.GetStringAsync("/dashboard");
        Assert.Matches("""<a\b[^>]*href="#contenido"[^>]*>""", html);
        Assert.Matches("<main\\b[^>]*id=\"contenido\"[^>]*tabindex=\"-1\"", html);
        Assert.Matches("<p\\b[^>]*id=\"announcement\"[^>]*role=\"status\"[^>]*aria-live=\"polite\"", html);
        Assert.Equal(2, Regex.Matches(html, "role=\"region\"\\s+aria-label=\"[^\"]+\"").Count);
        Assert.Equal(2, Regex.Matches(html, """<table>\s*<caption\b""" ).Count);
        foreach (var target in new[] { "estado", "actividad", "politicas" })
        {
            Assert.Contains("href=\"#" + target + "\"", html);
            Assert.Single(Regex.Matches(html, "\\bid=\"" + target + "\"").Cast<Match>());
        }
    }

    [Fact]
    public async Task Dashboard_serves_external_script_under_exact_same_origin_csp()
    {
        await using var app = Build();
        await app.StartAsync();
        using var client = Client(app);
        using var response = await client.GetAsync("/dashboard");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
            response.Headers.GetValues("Content-Security-Policy").Single());
        var html = await response.Content.ReadAsStringAsync();
        var scripts = Regex.Matches(html, @"<script\b[^>]*>[\s\S]*?</script\s*>", RegexOptions.IgnoreCase);
        var script = Assert.Single(scripts.Cast<Match>());
        Assert.Matches("""(?i)^<script\b[^>]*\bsrc\s*=\s*['"](?:/dashboard/|\./)?app\.js['"][^>]*>\s*</script\s*>$""", script.Value);
        using var js = await client.GetAsync("/dashboard/app.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Equal("text/javascript", js.Content.Headers.ContentType!.MediaType);
    }
}
