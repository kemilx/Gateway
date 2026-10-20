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
                Console.WriteLine("compare --output <artifacts/mvp05/NEW-directory> [--requests-per-arm N] [--repetitions N] [--concurrency N]\nLocal observational comparison, NOT performance acceptance. Defaults/ranges: 256/8..4096, 2/1..4, 6/1..6.");
                Console.WriteLine("hard-limit --output <artifacts/mvp05/directory> [--requests-per-concurrency N] [--repetitions N] [--memory-limit-mib N] [--headroom-percent N] [--max-concurrent-inspections N]\nWindows only, opt-in enforced process commit limit. Defaults: 64, 3, 512, 30, 6; same ranges as isolated. API/client excluded.");
                Console.WriteLine("isolated --output <artifacts/mvp05/directory> [--requests-per-concurrency N] [--repetitions N] [--memory-limit-mib N] [--headroom-percent N] [--max-concurrent-inspections N]\nValores por defecto/rangos: requests 64/8..100000; repetitions 3/1..10; memory MiB 512/64..65536; headroom percent 30/1..90; max-concurrent-inspections 8/4..8 (entero estricto). Solo configura workers sintéticos; no cambia Gateway/appsettings.\napi-worker (interno, sin opciones)\ngateway-worker --upstream <loopback-origin> --max-concurrent-inspections N (interno; 4..8)");
                Console.WriteLine("Local synthetic harness only; NOT for production.\nmemory --output <artifacts/mvp05/directory>\nhttp|all --output <artifacts/mvp05/directory> [--requests-per-concurrency N] (8..100000, default 16)\nmemory-worker --concurrency N (internal; N=1,2,4,8)\nOutput must remain under the local artifacts/mvp05 directory.\nMemory: fresh workers, MEASURED_NOT_SIZED. HTTP: shared-process observational memory.");
                return 0;
            }
            Common.Require(args.Length > 0);
            var command = args[0]; var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i += 2) Common.Require(i + 1 < args.Length && options.TryAdd(args[i], args[i + 1]));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            if (command == "compare")
                return await CompareHarness.Run(CompareHarness.Parse(options), timeout.Token) ? 0 : 1;
            if (command == "compare-gateway-worker")
            {
                Common.Require(options.Count == 1 && options.ContainsKey("--upstream"));
                await CompareWorkers.Run(options["--upstream"], timeout.Token);
                return 0;
            }
            if (command == "memory-worker")
            {
                Common.Require(options.Count == 1 && options.ContainsKey("--concurrency"));
                var concurrency = Number(options["--concurrency"]); Common.Require(Common.Concurrencies.Contains(concurrency));
                var report = await MemoryHarness.Worker(concurrency, timeout.Token);
                Console.WriteLine(JsonSerializer.Serialize(report, Common.Json)); return 0;
            }
            if (command is "api-worker" or "gateway-worker")
            {
                Common.Require(command == "api-worker" ? options.Count == 0 : options.Count == 2 && options.ContainsKey("--upstream") && options.ContainsKey("--max-concurrent-inspections"));
                var candidate = command == "api-worker" ? 8 : Number(options["--max-concurrent-inspections"]);
                Common.Require(candidate is >= 4 and <= 8);
                await IsolatedWorkers.Run(command == "api-worker" ? null : options["--upstream"], candidate, timeout.Token);
                return 0;
            }
            if (command is "isolated" or "hard-limit")
            {
                if (command == "hard-limit" && (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess))
                {
                    Console.Error.WriteLine("Hard-limit requires 64-bit Windows. No processes or reports created."); return 1;
                }
                Common.Require(options.Keys.All(k => k is "--output" or "--requests-per-concurrency" or "--repetitions" or "--memory-limit-mib" or "--headroom-percent" or "--max-concurrent-inspections"));
                Common.Require(options.TryGetValue("--output", out var isolatedOutput));
                int Option(string key, int fallback, int min, int max)
                {
                    var n = options.TryGetValue(key, out var text) ? Number(text) : fallback;
                    Common.Require(n >= min && n <= max); return n;
                }
                return await IsolatedHarness.Run(Common.OutputDirectory(isolatedOutput!),
                    Option("--requests-per-concurrency", 64, 8, 100000), Option("--repetitions", 3, 1, 10),
                    Option("--memory-limit-mib", 512, 64, 65536), Option("--headroom-percent", 30, 1, 90),
                    Option("--max-concurrent-inspections", command == "hard-limit" ? 6 : 8, 4, 8), timeout.Token, command == "hard-limit") ? 0 : 1;
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
