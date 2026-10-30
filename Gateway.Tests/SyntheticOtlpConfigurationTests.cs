using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyntheticTelemetry;
using Xunit;

namespace Gateway.Tests;

[Collection("SyntheticOtlp environment")]
public sealed class SyntheticOtlpConfigurationTests
{
    private const string ConfigurationError = "Synthetic OTLP configuration is invalid.";

    [Fact]
    public async Task Disabled_configuration_creates_no_runtime_and_ignores_endpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        using var environment = OtlpEnvironment.WithHostileValues();
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["SyntheticOtlp:Enabled"] = "false",
            ["SyntheticOtlp:Endpoint"] = $"http://127.0.0.1:{port}/not-an-allowed-path"
        });
        var services = new ServiceCollection();

        Register(services, configuration, SyntheticOtlpRole.Gateway);
        configuration["SyntheticOtlp:Enabled"] = "true";
        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<SyntheticOtlpSettings>());
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType.Namespace?.StartsWith("OpenTelemetry", StringComparison.Ordinal) == true ||
            descriptor.ServiceType.Namespace == "SyntheticTelemetry");
        Assert.Empty(provider.GetServices<IHostedService>());
        Assert.Empty(provider.GetServices<ILoggerProvider>());
        Assert.NotSame(accept, await Task.WhenAny(accept, Task.Delay(TimeSpan.FromMilliseconds(200))));
    }

    [Theory]
    [InlineData("http://127.0.0.1:4318/", "http://127.0.0.1:4318/v1/metrics", "http://127.0.0.1:4318/v1/logs")]
    [InlineData("http://[::1]:4318/", "http://[::1]:4318/v1/metrics", "http://[::1]:4318/v1/logs")]
    public void Enabled_configuration_accepts_only_canonical_literal_loopback_origin(
        string endpoint, string expectedMetrics, string expectedLogs)
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["SyntheticOtlp:Enabled"] = "true",
            ["SyntheticOtlp:Endpoint"] = endpoint
        });
        var services = new ServiceCollection();

        Register(services, configuration, SyntheticOtlpRole.Gateway);
        using var provider = services.BuildServiceProvider();
        var settings = Assert.IsType<SyntheticOtlpSettings>(provider.GetRequiredService<SyntheticOtlpSettings>());

        Assert.True(settings.Enabled);
        Assert.Equal(SyntheticOtlpRole.Gateway, settings.Role);
        Assert.Equal("gateway-synthetic", settings.ServiceName);
        Assert.Equal(expectedMetrics, settings.MetricsEndpoint.AbsoluteUri);
        Assert.Equal(expectedLogs, settings.LogsEndpoint.AbsoluteUri);
    }

    public static TheoryData<string?, string?, string?> InvalidConfigurations => new()
    {
        { "true", null, null },
        { "true", "http://localhost:4318/", null },
        { "true", "http://0.0.0.0:4318/", null },
        { "true", "http://10.0.0.1:4318/", null },
        { "true", "http://127.0.0.2:4318/", null },
        { "true", "http://[::ffff:127.0.0.1]:4318/", null },
        { "true", "http://127.000.000.001:4318/", null },
        { "true", "http://[0:0:0:0:0:0:0:1]:4318/", null },
        { "true", "http://127.0.0.1/", null },
        { "true", "http://127.0.0.1:0/", null },
        { "true", "http://127.0.0.1:65536/", null },
        { "true", "https://127.0.0.1:4318/", null },
        { "true", "http://user@127.0.0.1:4318/", null },
        { "true", "http://127.0.0.1:4318/path", null },
        { "true", "http://127.0.0.1:4318/?query=value", null },
        { "true", "http://127.0.0.1:4318/#fragment", null },
        { "true", "http://127.0.0.1:4318/", "unexpected" }
    };

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void Unknown_keys_and_invalid_endpoint_fail_during_build_before_listening(
        string? enabled, string? endpoint, string? unknown)
    {
        var values = new Dictionary<string, string?>
        {
            ["SyntheticOtlp:Enabled"] = enabled,
            ["SyntheticOtlp:Endpoint"] = endpoint
        };
        if (unknown is not null)
            values["SyntheticOtlp:Unexpected"] = unknown;
        var configuration = Configuration(values);
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(
            () => Register(services, configuration, SyntheticOtlpRole.Gateway));

        Assert.Equal(ConfigurationError, error.Message);
        Assert.Null(error.InnerException);
        if (endpoint is not null)
            Assert.DoesNotContain(endpoint, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType.Namespace == "SyntheticTelemetry");
    }

    [Fact]
    public void Configuration_is_frozen_and_otel_environment_cannot_override_contract()
    {
        using var environment = OtlpEnvironment.WithHostileValues();
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["SyntheticOtlp:Enabled"] = "true",
            ["SyntheticOtlp:Endpoint"] = "http://127.0.0.1:4318/"
        });
        var services = new ServiceCollection();

        Register(services, configuration, SyntheticOtlpRole.TestApi);
        configuration["SyntheticOtlp:Enabled"] = "false";
        configuration["SyntheticOtlp:Endpoint"] = "http://127.0.0.1:9999/";
        environment.ReplaceWithDifferentHostileValues();
        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<SyntheticOtlpSettings>();

        Assert.True(settings.Enabled);
        Assert.Equal(SyntheticOtlpRole.TestApi, settings.Role);
        Assert.Equal("test-api-synthetic", settings.ServiceName);
        Assert.Equal("http://127.0.0.1:4318/v1/metrics", settings.MetricsEndpoint.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:4318/v1/logs", settings.LogsEndpoint.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(2), settings.ExportTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), settings.MetricExportInterval);
        Assert.Equal(TimeSpan.FromSeconds(3), settings.ShutdownTimeout);
        Assert.Equal(256, settings.LogQueueCapacity);
        Assert.Equal(64, settings.LogBatchSize);
        Assert.Equal(TimeSpan.FromSeconds(1), settings.LogExportDelay);
        Assert.Equal(256, settings.MetricCardinalityLimit);
    }

    private static IConfigurationRoot Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static void Register(IServiceCollection services, IConfiguration configuration, SyntheticOtlpRole role) =>
        services.AddLogging(logging => SyntheticOtlpRegistration.Add(services, logging, configuration, role));

    private sealed class OtlpEnvironment : IDisposable
    {
        private static readonly string[] Names =
        [
            "OTEL_SDK_DISABLED", "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT",
            "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "OTEL_EXPORTER_OTLP_PROTOCOL", "OTEL_EXPORTER_OTLP_TIMEOUT",
            "OTEL_METRIC_EXPORT_INTERVAL", "OTEL_METRIC_EXPORT_TIMEOUT", "OTEL_BLRP_MAX_QUEUE_SIZE",
            "OTEL_BLRP_MAX_EXPORT_BATCH_SIZE", "OTEL_BLRP_SCHEDULE_DELAY", "OTEL_BLRP_EXPORT_TIMEOUT",
            "OTEL_SERVICE_NAME", "OTEL_RESOURCE_ATTRIBUTES"
        ];

        private readonly Dictionary<string, string?> originals = Names.ToDictionary(
            name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);

        private OtlpEnvironment() => Set("hostile-before");

        public static OtlpEnvironment WithHostileValues() => new();

        public void ReplaceWithDifferentHostileValues() => Set("hostile-after");

        public void Dispose()
        {
            foreach (var pair in originals)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        private static void Set(string marker)
        {
            foreach (var name in Names)
                Environment.SetEnvironmentVariable(name, marker);
        }
    }
}

[CollectionDefinition("SyntheticOtlp environment", DisableParallelization = true)]
public sealed class SyntheticOtlpEnvironmentCollection;
