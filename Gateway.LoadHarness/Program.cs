using System.Globalization;
using System.Text.Json;

namespace Gateway.LoadHarness;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        try
        {
            if (args is ["--help"])
            {
                Console.WriteLine("Local synthetic harness only; NOT for production.\nmemory --output <artifacts/mvp05/directory>\nhttp|all --output <artifacts/mvp05/directory> [--requests-per-concurrency N] (8..100000, default 16)\nmemory-worker --concurrency N (internal; N=1,2,4,8)\nOutput must remain under the local artifacts/mvp05 directory.\nMemory: fresh workers, MEASURED_NOT_SIZED. HTTP: shared-process observational memory.");
                return 0;
            }
            Common.Require(args.Length > 0);
            var command = args[0]; var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i += 2) Common.Require(i + 1 < args.Length && options.TryAdd(args[i], args[i + 1]));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            if (command == "memory-worker")
            {
                Common.Require(options.Count == 1 && options.ContainsKey("--concurrency"));
                var concurrency = Number(options["--concurrency"]); Common.Require(Common.Concurrencies.Contains(concurrency));
                var report = await MemoryHarness.Worker(concurrency, timeout.Token);
                Console.WriteLine(JsonSerializer.Serialize(report, Common.Json)); return 0;
            }
            Common.Require(command is "memory" or "http" or "all");
            Common.Require(options.TryGetValue("--output", out var outputValue) && !string.IsNullOrWhiteSpace(outputValue));
            Common.Require(options.Keys.All(k => k == "--output" || (command != "memory" && k == "--requests-per-concurrency")));
            var output = Common.OutputDirectory(outputValue!);
            var requests = options.TryGetValue("--requests-per-concurrency", out var value) ? Number(value) : 16;
            Common.Require(requests is >= 8 and <= 100000);
            if (command is "memory" or "all") await MemoryHarness.Run(output, timeout.Token);
            if (command is "http" or "all") await HttpHarness.Run(output, requests, timeout.Token);
            if (command == "all") await Common.Atomic(output, "summary.md", "# MVP-05\n\nMemory and HTTP: passed. See memory.md and http.md.\nMEASURED_NOT_SIZED: no target memory limit. HTTP memory is shared-process observational.\n");
            Console.WriteLine("Harness passed."); return 0;
        }
        catch
        {
            if (args.FirstOrDefault() == "memory-worker")
                Console.WriteLine(JsonSerializer.Serialize(new { schema = "gateway.mvp05.memory-worker.v1", status = "failed" }, Common.Json));
            Console.Error.WriteLine("Harness failed: invalid arguments, timeout, or scenario invariant. No sensitive diagnostics emitted.");
            return 1;
        }
    }
    private static int Number(string value) => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
}
