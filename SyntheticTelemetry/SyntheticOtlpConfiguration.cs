using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

namespace SyntheticTelemetry;

public enum SyntheticOtlpRole
{
    Gateway,
    TestApi
}

public sealed class SyntheticOtlpSettings
{
    internal SyntheticOtlpSettings(SyntheticOtlpRole role, Uri endpoint)
    {
        Role = role;
        Endpoint = endpoint;
        MetricsEndpoint = new Uri(endpoint, "v1/metrics");
        LogsEndpoint = new Uri(endpoint, "v1/logs");
    }

    public bool Enabled => true;
    public SyntheticOtlpRole Role { get; }
    public string ServiceName => Role == SyntheticOtlpRole.Gateway ? "gateway-synthetic" : "test-api-synthetic";
    public Uri Endpoint { get; }
    public Uri MetricsEndpoint { get; }
    public Uri LogsEndpoint { get; }
    public TimeSpan ExportTimeout => TimeSpan.FromSeconds(2);
    public TimeSpan MetricExportInterval => TimeSpan.FromSeconds(5);
    public TimeSpan ShutdownTimeout => TimeSpan.FromSeconds(3);
    public int LogQueueCapacity => 256;
    public int LogBatchSize => 64;
    public TimeSpan LogExportDelay => TimeSpan.FromSeconds(1);
    public int MetricCardinalityLimit => 256;
}

internal static class SyntheticOtlpConfiguration
{
    internal const string Error = "Synthetic OTLP configuration is invalid.";
    private const string SectionName = "SyntheticOtlp";

    internal static SyntheticOtlpSettings? Capture(IConfiguration configuration, SyntheticOtlpRole role)
    {
        if (role is not SyntheticOtlpRole.Gateway and not SyntheticOtlpRole.TestApi)
            throw Invalid();

        var section = configuration.GetSection(SectionName);
        if (section.Value is not null)
            throw Invalid();

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in section.GetChildren())
        {
            if (child.Key is not ("Enabled" or "Endpoint") || child.GetChildren().Any() ||
                !values.TryAdd(child.Key, child.Value))
                throw Invalid();
        }

        var enabledText = values.GetValueOrDefault("Enabled");
        var enabled = false;
        if (enabledText is not null &&
            (enabledText.Length == 0 || enabledText != enabledText.Trim() || !bool.TryParse(enabledText, out enabled)))
            throw Invalid();

        if (!enabled)
            return null;

        var endpointText = values.GetValueOrDefault("Endpoint");
        if (endpointText is null || !TryCreateEndpoint(endpointText, out var endpoint))
            throw Invalid();

        return new SyntheticOtlpSettings(role, endpoint);
    }

    private static bool TryCreateEndpoint(string value, out Uri endpoint)
    {
        endpoint = null!;
        if (!value.StartsWith("http://", StringComparison.Ordinal) || value != value.Trim())
            return false;

        var authorityAndPath = value["http://".Length..];
        var pathIndex = authorityAndPath.IndexOfAny(['/', '?', '#']);
        var authority = pathIndex < 0 ? authorityAndPath : authorityAndPath[..pathIndex];
        var suffix = pathIndex < 0 ? string.Empty : authorityAndPath[pathIndex..];
        if (suffix is not ("" or "/") || authority.Length == 0 || authority.Contains('@'))
            return false;

        string host;
        string portText;
        var bracketed = authority.StartsWith("[", StringComparison.Ordinal);
        if (bracketed)
        {
            var closeBracket = authority.IndexOf(']');
            if (closeBracket <= 1 || closeBracket + 1 >= authority.Length || authority[closeBracket + 1] != ':')
                return false;
            host = authority[1..closeBracket];
            portText = authority[(closeBracket + 2)..];
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            if (colon <= 0 || authority.IndexOf(':') != colon)
                return false;
            host = authority[..colon];
            portText = authority[(colon + 1)..];
        }

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535 || portText != port.ToString(CultureInfo.InvariantCulture) ||
            !IPAddress.TryParse(host, out var address) || address.IsIPv4MappedToIPv6)
            return false;

        string canonicalHost;
        if (address.AddressFamily == AddressFamily.InterNetwork && address.Equals(IPAddress.Loopback) && !bracketed)
            canonicalHost = address.ToString();
        else if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.Equals(IPAddress.IPv6Loopback) && bracketed)
            canonicalHost = address.ToString();
        else
            return false;

        if (!string.Equals(host, canonicalHost, StringComparison.Ordinal))
            return false;

        var canonicalOrigin = bracketed
            ? $"http://[{canonicalHost}]:{port.ToString(CultureInfo.InvariantCulture)}"
            : $"http://{canonicalHost}:{port.ToString(CultureInfo.InvariantCulture)}";
        if (value != canonicalOrigin && value != canonicalOrigin + "/")
            return false;

        endpoint = new Uri(canonicalOrigin + "/", UriKind.Absolute);
        return true;
    }

    private static InvalidOperationException Invalid() => new(Error);
}
