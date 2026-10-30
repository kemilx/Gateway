using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SyntheticTelemetry;

public static class SyntheticOtlpRegistration
{
    public static void Add(
        IServiceCollection services,
        ILoggingBuilder logging,
        IConfiguration configuration,
        SyntheticOtlpRole role)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = SyntheticOtlpConfiguration.Capture(configuration, role);
        if (settings is null)
            return;

        services.AddSingleton(settings);
        services.AddSingleton<ILoggerProvider, SyntheticOtlpLoggerProvider>();
        if (role == SyntheticOtlpRole.Gateway)
            services.AddSingleton(provider => new SyntheticOtlpMetrics(provider.GetRequiredService<SyntheticOtlpSettings>()));
    }

    public static void Start(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var settings = services.GetService<SyntheticOtlpSettings>();
        if (settings?.Role == SyntheticOtlpRole.Gateway)
            _ = services.GetRequiredService<SyntheticOtlpMetrics>();
    }
}
