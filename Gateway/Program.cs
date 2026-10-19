using Gateway;

try
{
    await using var app = GatewayHost.Build(args);
    await app.RunAsync();
    return 0;
}
catch (Exception)
{
    // Startup failures (including malformed URLs) must not echo configuration,
    // certificate paths, endpoint names or exception details to the console.
    Console.Error.WriteLine("Gateway could not start or run with the supplied configuration.");
    return 1;
}
