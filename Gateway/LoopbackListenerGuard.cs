using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;

namespace Gateway;

// Enforce at the transport boundary: Kestrel has already resolved URL precedence,
// localhost, configuration endpoints and code-based Listen overrides here.
internal static class LoopbackListenerGuard
{
    internal const string Error = "Gateway requires verifiable loopback IP listeners using the standard Kestrel socket transport.";

    internal static void Register(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IConnectionListenerFactory)
                     || d.ServiceType == typeof(IMultiplexedConnectionListenerFactory)).ToArray())
        {
            services.Remove(descriptor);
            if (descriptor.ServiceType == typeof(IConnectionListenerFactory)
                && descriptor.ImplementationType == typeof(SocketTransportFactory))
                services.AddSingleton<IConnectionListenerFactory>(sp =>
                    new GuardedSockets(ActivatorUtilities.CreateInstance<SocketTransportFactory>(sp)));
            else if (descriptor.ServiceType == typeof(IMultiplexedConnectionListenerFactory))
                services.AddSingleton<IMultiplexedConnectionListenerFactory, RejectedMultiplexedTransport>();
            else if (descriptor.ImplementationType?.FullName == "Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes.Internal.NamedPipeTransportFactory")
            {
                // Registered by default on Windows; all endpoints go through the
                // socket guard instead, which rejects named-pipe endpoints.
            }
            else
                throw new InvalidOperationException(Error);
        }

        // A custom socket creator could ignore even a validated IPEndPoint.
        services.PostConfigure<SocketTransportOptions>(options =>
        {
            if (options.CreateBoundListenSocket != SocketTransportOptions.CreateDefaultBoundListenSocket)
                throw new InvalidOperationException(Error);
        });
    }

    internal static void ValidateServer(IServer server)
    {
        // Alternative servers do not necessarily use the guarded transport factories.
        if (server.GetType().Assembly != typeof(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServer).Assembly)
            throw new InvalidOperationException(Error);
    }

    internal static void Validate(EndPoint endpoint)
    {
        if (endpoint is not IPEndPoint ip || !IPAddress.IsLoopback(ip.Address)
            || ip.Address.IsIPv4MappedToIPv6)
            throw new InvalidOperationException(Error);
    }

    private sealed class GuardedSockets(SocketTransportFactory inner) : IConnectionListenerFactory
    {
        public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
        {
            Validate(endpoint);
            return inner.BindAsync(endpoint, cancellationToken);
        }
    }

    // HTTP/3 is not part of this local slice; do not leave a second unguarded transport.
    private sealed class RejectedMultiplexedTransport : IMultiplexedConnectionListenerFactory
    {
        public ValueTask<IMultiplexedConnectionListener> BindAsync(EndPoint endpoint,
            IFeatureCollection? features = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(Error);
    }
}
